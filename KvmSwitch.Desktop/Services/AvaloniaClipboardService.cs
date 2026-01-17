using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using Microsoft.Extensions.Logging;

namespace KvmSwitch.Desktop.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    private const long MaxClipboardBytes = 1L * 1024 * 1024 * 1024;

    private readonly IDataNetworkService _dataNetworkService;
    private readonly IFileTransferService _fileTransferService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AvaloniaClipboardService> _logger;
    private readonly DispatcherTimer _timer;
    private bool _isRunning;
    private bool _isUpdating;
    private int _pollInProgress;
    private string? _lastText;
    private int _lastHash;
    private int _lastFilesHash;
    private int _lastFilesCount;
    private int _lastFormatsHash;
    private int _lastFormatsCount;

    private sealed class FileSnapshot
    {
        public FileSnapshot(string path, long size, long lastWriteTicks)
        {
            Path = path;
            Size = size;
            LastWriteTicks = lastWriteTicks;
        }

        public string Path { get; }
        public long Size { get; }
        public long LastWriteTicks { get; }
    }

    public event EventHandler<string>? ClipboardTextChanged;

    public AvaloniaClipboardService(
        IDataNetworkService dataNetworkService,
        IFileTransferService fileTransferService,
        ISettingsService settingsService,
        ILogger<AvaloniaClipboardService> logger)
    {
        _dataNetworkService = dataNetworkService ?? throw new ArgumentNullException(nameof(dataNetworkService));
        _fileTransferService = fileTransferService ?? throw new ArgumentNullException(nameof(fileTransferService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;

        _dataNetworkService.MessageReceived += OnMessageReceived;
        _fileTransferService.FilesReceived += OnFilesReceived;
    }

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _logger.LogInformation("Clipboard service started.");
        _timer.Start();
        StartFileTransferService();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _logger.LogInformation("Clipboard service stopped.");
        _timer.Stop();
        StopFileTransferService();
    }

    private void StartFileTransferService()
    {
        try
        {
            if (_dataNetworkService.IsServer)
            {
                _logger.LogInformation("Starting file transfer service in server mode.");
                _ = _fileTransferService.StartServerAsync();
                return;
            }

            var hostAddress = GetConfiguredHostAddress();
            if (string.IsNullOrWhiteSpace(hostAddress))
            {
                _logger.LogInformation("Starting file transfer service in client auto-discovery mode.");
                _ = _fileTransferService.StartClientAsync();
            }
            else
            {
                _logger.LogInformation("Starting file transfer service in client mode targeting {Host}.", hostAddress);
                _ = _fileTransferService.StartClientAsync(hostAddress);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start file transfer service.");
        }
    }

    private void StopFileTransferService()
    {
        try
        {
            _fileTransferService.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop file transfer service.");
        }
    }

    private string? GetConfiguredHostAddress()
    {
        try
        {
            return _settingsService.Load().ReceiverHostIp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read settings for file transfer.");
            return null;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _ = PollClipboardAsync();
    }

    private async Task PollClipboardAsync()
    {
        if (!_isRunning || _isUpdating)
        {
            return;
        }

        if (Interlocked.Exchange(ref _pollInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var clipboard = TryGetClipboard();
            if (clipboard == null)
            {
                _logger.LogDebug("Clipboard instance unavailable.");
                return;
            }

            var formats = await TryGetClipboardFormatsAsync(clipboard).ConfigureAwait(true);
            var formatsChanged = TrackClipboardFormats(formats);

            var fileSnapshots = await TryGetClipboardFilesAsync(clipboard).ConfigureAwait(true);
            if (fileSnapshots is { Count: > 0 })
            {
                var fileHash = ComputeFilesHash(fileSnapshots);
                if (IsFilesUnchanged(fileHash, fileSnapshots.Count))
                {
                    return;
                }

                UpdateFilesState(fileHash, fileSnapshots.Count);

                var paths = new List<string>(fileSnapshots.Count);
                long totalBytes = 0;
                foreach (var file in fileSnapshots)
                {
                    totalBytes += file.Size;
                    paths.Add(file.Path);
                }

                _logger.LogInformation(
                    "Clipboard files detected ({Count} files, {TotalBytes} bytes).",
                    fileSnapshots.Count,
                    totalBytes);
                _logger.LogDebug("Clipboard file list: {Files}", FormatList(paths, 6));

                if (totalBytes > MaxClipboardBytes)
                {
                    _logger.LogWarning("Clipboard files exceed the 1 GB limit ({TotalBytes} bytes).", totalBytes);
                    return;
                }

                try
                {
                    if (!_fileTransferService.IsConnected)
                    {
                        _logger.LogWarning("File transfer service not connected; clipboard files may not send.");
                    }

                    await _fileTransferService.SendFilesAsync(paths).ConfigureAwait(false);
                    _logger.LogInformation("Clipboard files sent.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send clipboard files.");
                }

                return;
            }

            if (formatsChanged && formats is { Count: > 0 } && ContainsFileFormats(formats))
            {
                _logger.LogDebug("Clipboard reports file formats but no local files could be resolved.");
            }

            string text;
            try
            {
                text = await ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true) ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read clipboard text.");
                return;
            }

            if (string.IsNullOrEmpty(text) && formatsChanged && formats is { Count: > 0 })
            {
                if (ContainsPotentialBitmapFormat(formats))
                {
                    _logger.LogInformation("Clipboard contains bitmap/image data. Bitmap sync is not supported yet.");
                }
                else if (!ContainsKnownTextOrFileFormats(formats))
                {
                    _logger.LogDebug("Clipboard contains unsupported formats: {Formats}", FormatList(formats, 6));
                }
            }

            if (!HasChanged(text))
            {
                return;
            }

            UpdateLocalState(text);
            _logger.LogDebug("Clipboard text updated (length {Length}).", text.Length);
            ClipboardTextChanged?.Invoke(this, text);

            try
            {
                await _dataNetworkService.SendAsync(new ClipboardMessage { Text = text }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send clipboard text.");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
        }
    }

    private void OnMessageReceived(object? sender, (object Message, Guid ClientId) args)
    {
        if (args.Message is ClipboardMessage clipboardMessage)
        {
            _logger.LogDebug("Clipboard text received from {ClientId} (length {Length}).", args.ClientId, clipboardMessage.Text?.Length ?? 0);
            ApplyRemoteText(clipboardMessage.Text);
        }
    }

    private void OnFilesReceived(object? sender, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Received {Count} file(s) from remote clipboard.", files.Count);
        _logger.LogDebug("Received file list: {Files}", FormatList(files, 6));
        _isUpdating = true;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var clipboard = TryGetClipboard();
                if (clipboard == null)
                {
                    return;
                }

                var storageProvider = TryGetStorageProvider();
                if (storageProvider == null)
                {
                    _logger.LogWarning("Storage provider unavailable for clipboard files.");
                    return;
                }

                var storageItems = new List<IStorageItem>();
                foreach (var path in files)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var item = await storageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true);
                    if (item != null)
                    {
                        storageItems.Add(item);
                    }
                }

                if (storageItems.Count == 0)
                {
                    return;
                }

                await ClipboardExtensions.SetFilesAsync(clipboard, storageItems).ConfigureAwait(true);
                if (!_fileTransferService.IsServer)
                {
                    UpdateFilesStateFromPaths(files);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply remote clipboard files.");
            }
            finally
            {
                _isUpdating = false;
            }
        });
    }

    private void ApplyRemoteText(string? text)
    {
        var normalized = text ?? string.Empty;
        if (!HasChanged(normalized))
        {
            return;
        }

        _logger.LogDebug("Applying remote clipboard text (length {Length}).", normalized.Length);
        _isUpdating = true;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var clipboard = TryGetClipboard();
                if (clipboard == null)
                {
                    return;
                }

                await clipboard.SetTextAsync(normalized);
                UpdateLocalState(normalized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply remote clipboard text.");
            }
            finally
            {
                _isUpdating = false;
            }
        });
    }

    private bool HasChanged(string text)
    {
        var hash = StringComparer.Ordinal.GetHashCode(text);
        return hash != _lastHash || !string.Equals(text, _lastText, StringComparison.Ordinal);
    }

    private void UpdateLocalState(string text)
    {
        _lastText = text;
        _lastHash = StringComparer.Ordinal.GetHashCode(text);
    }

    private async Task<IReadOnlyList<FileSnapshot>?> TryGetClipboardFilesAsync(IClipboard clipboard)
    {
        IStorageItem[]? items;
        try
        {
            items = await ClipboardExtensions.TryGetFilesAsync(clipboard).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read clipboard files.");
            return null;
        }

        if (items is null || items.Length == 0)
        {
            return null;
        }

        var files = new List<FileSnapshot>(items.Length);
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                files.Add(new FileSnapshot(path, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read clipboard file metadata for {Path}.", path);
            }
        }

        return files.Count == 0 ? null : files;
    }

    private async Task<IReadOnlyList<string>?> TryGetClipboardFormatsAsync(IClipboard clipboard)
    {
        try
        {
            var formats = await ClipboardExtensions.GetDataFormatsAsync(clipboard).ConfigureAwait(true);
            if (formats == null)
            {
                return null;
            }

            if (formats.Count == 0)
            {
                return Array.Empty<string>();
            }

            var identifiers = new List<string>(formats.Count);
            foreach (var format in formats)
            {
                var identifier = format.Identifier;
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    identifiers.Add(identifier);
                }
                else
                {
                    identifiers.Add(format.ToString());
                }
            }

            return identifiers;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read clipboard formats.");
            return null;
        }
    }

    private bool TrackClipboardFormats(IReadOnlyList<string>? formats)
    {
        if (formats == null)
        {
            return false;
        }

        if (formats.Count == 0)
        {
            if (IsFormatsUnchanged(0, 0))
            {
                return false;
            }

            UpdateFormatsState(0, 0);
            _logger.LogDebug("Clipboard formats cleared.");
            return true;
        }

        var hash = ComputeFormatsHash(formats);
        if (IsFormatsUnchanged(hash, formats.Count))
        {
            return false;
        }

        UpdateFormatsState(hash, formats.Count);
        _logger.LogDebug("Clipboard formats detected ({Count}): {Formats}", formats.Count, FormatList(formats, 6));
        return true;
    }

    private static int ComputeFilesHash(IReadOnlyList<FileSnapshot> files)
    {
        var hash = new HashCode();
        foreach (var file in files)
        {
            hash.Add(file.Path, StringComparer.OrdinalIgnoreCase);
            hash.Add(file.Size);
            hash.Add(file.LastWriteTicks);
        }

        return hash.ToHashCode();
    }

    private static int ComputeFormatsHash(IReadOnlyList<string> formats)
    {
        var ordered = new List<string>(formats);
        ordered.Sort(StringComparer.OrdinalIgnoreCase);

        var hash = new HashCode();
        foreach (var format in ordered)
        {
            hash.Add(format, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    private bool IsFilesUnchanged(int hash, int count)
    {
        return hash == _lastFilesHash && count == _lastFilesCount;
    }

    private void UpdateFilesState(int hash, int count)
    {
        _lastFilesHash = hash;
        _lastFilesCount = count;
    }

    private bool IsFormatsUnchanged(int hash, int count)
    {
        return hash == _lastFormatsHash && count == _lastFormatsCount;
    }

    private void UpdateFormatsState(int hash, int count)
    {
        _lastFormatsHash = hash;
        _lastFormatsCount = count;
    }

    private void UpdateFilesStateFromPaths(IReadOnlyList<string> paths)
    {
        var snapshots = new List<FileSnapshot>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                snapshots.Add(new FileSnapshot(path, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read clipboard file metadata for {Path}.", path);
            }
        }

        if (snapshots.Count == 0)
        {
            return;
        }

        var hash = ComputeFilesHash(snapshots);
        UpdateFilesState(hash, snapshots.Count);
    }

    private static string FormatList(IReadOnlyList<string> items, int maxItems)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var take = Math.Min(items.Count, maxItems);
        var preview = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            if (!string.IsNullOrWhiteSpace(items[i]))
            {
                preview.Add(items[i]);
            }
        }

        var summary = string.Join(", ", preview);
        if (items.Count > maxItems)
        {
            summary = $"{summary} (+{items.Count - maxItems} more)";
        }

        return summary;
    }

    private static bool ContainsPotentialBitmapFormat(IReadOnlyList<string> formats)
    {
        foreach (var format in formats)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                continue;
            }

            if (format.Contains("bitmap", StringComparison.OrdinalIgnoreCase)
                || format.Contains("image", StringComparison.OrdinalIgnoreCase)
                || format.Contains("png", StringComparison.OrdinalIgnoreCase)
                || format.Contains("dib", StringComparison.OrdinalIgnoreCase)
                || format.Contains("jpg", StringComparison.OrdinalIgnoreCase)
                || format.Contains("jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsFileFormats(IReadOnlyList<string> formats)
    {
        var fileIdentifier = DataFormat.File.Identifier;
        foreach (var format in formats)
        {
            if (string.Equals(format, fileIdentifier, StringComparison.OrdinalIgnoreCase)
                || format.Contains("file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsKnownTextOrFileFormats(IReadOnlyList<string> formats)
    {
        var textIdentifier = DataFormat.Text.Identifier;
        var fileIdentifier = DataFormat.File.Identifier;
        foreach (var format in formats)
        {
            if (string.Equals(format, textIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, fileIdentifier, StringComparison.OrdinalIgnoreCase)
                || format.Contains("text", StringComparison.OrdinalIgnoreCase)
                || format.Contains("file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IClipboard? TryGetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }

    private static IStorageProvider? TryGetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }

        return null;
    }
}
