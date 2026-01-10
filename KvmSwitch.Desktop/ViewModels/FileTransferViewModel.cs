using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace KvmSwitch.Desktop.ViewModels;

public partial class FileTransferViewModel : ViewModelBase
{
    private const int TransferPort = 54546;
    private const int ChunkSize = 1024 * 1024;
    private const int TimeoutSeconds = 120;
    private static readonly TimeSpan AutoCleanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TempZipMaxAge = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SettingsOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions HeaderOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<FileTransferViewModel> _logger;
    private readonly string _settingsPath;
    private readonly object _settingsLock = new();
    private readonly DispatcherTimer _autoCleanTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _isLoadingSettings;

    private TcpListener? _listener;
    private CancellationTokenSource? _receiverCts;
    private Task? _receiverTask;

    private CancellationTokenSource? _sendCts;
    private Task? _sendTask;

    public event Action? RequestClose;

    public ObservableCollection<string> Files { get; } = new();

    [ObservableProperty]
    private string receiverDestination = string.Empty;

    [ObservableProperty]
    private string receiverSharedKey = string.Empty;

    [ObservableProperty]
    private string senderIp = string.Empty;

    [ObservableProperty]
    private string senderSharedKey = string.Empty;

    [ObservableProperty]
    private bool useChecksum;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSendCommand))]
    private bool isSending;

    [ObservableProperty]
    private bool isReceiverRunning;

    [ObservableProperty]
    private string receiverStatus = "Állapot: leállítva";

    [ObservableProperty]
    private string receiverButtonText = "Fogadó indítása";

    [ObservableProperty]
    private string localIp = "127.0.0.1";

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private string progressLabel = "Nincs aktív átvitel.";

    [ObservableProperty]
    private string logOutput = string.Empty;

    public int Port => TransferPort;

    public FileTransferViewModel(ILogger<FileTransferViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsPath = Path.Combine(appData, "KvmSwitch", "lan-transfer.json");

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        _autoCleanTimer = new DispatcherTimer { Interval = AutoCleanInterval };
        _autoCleanTimer.Tick += (_, _) => AutoClean();
        _autoCleanTimer.Start();

        ReceiverDestination = GetDefaultDownloadPath();
        SenderIp = GetLocalIpGuess();
        LocalIp = SenderIp;

        LoadSettings();

        Files.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Back()
    {
        SaveSettings();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ToggleReceiver()
    {
        if (IsReceiverRunning)
        {
            StopReceiver();
            return;
        }

        StartReceiver();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Files.Count == 0)
        {
            AppendLog("Nincs mit küldeni.");
            return;
        }

        var host = SenderIp?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            AppendLog("Add meg a fogadó IP-címét.");
            return;
        }

        SetIsSending(true);
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;

        var items = Files.ToList();
        var tempFiles = new List<string>();

        _sendTask = Task.Run(async () =>
        {
            try
            {
                var prepared = PrepareSendItems(items, tempFiles, token);
                foreach (var path in prepared)
                {
                    token.ThrowIfCancellationRequested();
                    await SendFileAsync(host, path, token).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var temp in tempFiles)
                {
                    SafeRemove(temp);
                }
            }
        }, token);

        try
        {
            await _sendTask.ConfigureAwait(false);
            AppendLog("Küldés kész.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Küldés megszakítva.");
        }
        catch (Exception ex)
        {
            AppendLog($"Küldési hiba: {ex.Message}");
            _logger.LogWarning(ex, "Küldési hiba.");
        }
        finally
        {
            ResetProgress();
            SetIsSending(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopSend))]
    private void StopSend()
    {
        if (_sendCts is null)
        {
            return;
        }

        AppendLog("Megszakítás kérve.");
        _sendCts.Cancel();
    }

    [RelayCommand]
    private void CleanPartFiles()
    {
        var removed = CleanPartFilesInternal(silent: false);
        if (removed == 0)
        {
            AppendLog("Nincs törölhető .part fájl.");
        }
    }

    public void AddFiles(IEnumerable<string> files)
    {
        foreach (var path in files)
        {
            if (!Files.Contains(path))
            {
                Files.Add(path);
            }
        }
    }

    public void AddDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && !Files.Contains(directory))
        {
            Files.Add(directory);
        }
    }

    public void RemoveFiles(IEnumerable<string> files)
    {
        foreach (var path in files.ToList())
        {
            _ = Files.Remove(path);
        }
    }

    public void ClearFiles()
    {
        Files.Clear();
    }

    public void Shutdown()
    {
        SaveSettings();
        _autoCleanTimer.Stop();
        _saveTimer.Stop();
        StopReceiver();
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = null;
        _sendTask = null;
    }

    partial void OnIsReceiverRunningChanged(bool value)
    {
        ReceiverButtonText = value ? "Fogadó leállítása" : "Fogadó indítása";
    }

    partial void OnReceiverDestinationChanged(string value)
    {
        ScheduleSave();
    }

    partial void OnSenderIpChanged(string value)
    {
        ScheduleSave();
    }

    partial void OnUseChecksumChanged(bool value)
    {
        ScheduleSave();
    }

    private bool CanSend()
    {
        return !IsSending && Files.Count > 0;
    }

    private bool CanStopSend()
    {
        return IsSending;
    }

    private void SetIsSending(bool value)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsSending = value;
        }
        else
        {
            Dispatcher.UIThread.Post(() => IsSending = value);
        }
    }

    private void StartReceiver()
    {
        if (string.IsNullOrWhiteSpace(ReceiverDestination) || !Directory.Exists(ReceiverDestination))
        {
            AppendLog("Érvénytelen célmappa.");
            return;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Any, TransferPort);
            _listener.Start();
            _receiverCts = new CancellationTokenSource();
            _receiverTask = Task.Run(() => ReceiverLoop(_receiverCts.Token), _receiverCts.Token);
            IsReceiverRunning = true;
            ReceiverStatus = $"Állapot: figyel {GetLocalIpGuess()}:{TransferPort}";
            AppendLog($"Fogadó figyel ezen a porton: {TransferPort}.");
            _ = TryOpenFirewallPortAsync(TransferPort);
        }
        catch (Exception ex)
        {
            AppendLog($"Fogadási hiba: {ex.Message}");
            _logger.LogWarning(ex, "Fogadó indítása sikertelen.");
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        if (!IsReceiverRunning)
        {
            return;
        }

        try
        {
            _receiverCts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop receiver.");
        }
        finally
        {
            _receiverTask = null;
            _receiverCts?.Dispose();
            _receiverCts = null;
            _listener = null;
            IsReceiverRunning = false;
            ReceiverStatus = "Állapot: leállítva";
            AppendLog("Fogadó leállítva.");
        }
    }

    private void ReceiverLoop(CancellationToken token)
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (client is null)
                {
                    continue;
                }

                _ = Task.Run(() => HandleReceiverClient(client, token), token);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Fogadó ciklus hiba: {ex.Message}");
            _logger.LogWarning(ex, "Fogadó ciklus hiba.");
        }
    }

    private void HandleReceiverClient(TcpClient client, CancellationToken token)
    {
        string? partPath = null;

        try
        {
            client.ReceiveTimeout = TimeoutSeconds * 1000;
            client.SendTimeout = TimeoutSeconds * 1000;

            using var stream = client.GetStream();
            var headerBytes = ReadLine(stream, token);
            if (headerBytes is null)
            {
                AppendLog("Kapcsolat lezárult fejléc nélkül.");
                return;
            }

            var header = JsonSerializer.Deserialize<TransferHeader>(headerBytes, HeaderOptions);
            if (header is null)
            {
                AppendLog("Érvénytelen fejléc érkezett.");
                WriteLine(stream, "ERR:HEADER\n");
                return;
            }

            var expectedKey = ComputeSha256Hex(ReceiverSharedKey);
            if (!string.IsNullOrEmpty(expectedKey) && header.SharedKeyHash != expectedKey)
            {
                AppendLog("A közös kulcs nem egyezik.");
                WriteLine(stream, "ERR:KEY\n");
                return;
            }

            if (!string.Equals(header.Kind, "file", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Ismeretlen átvitel típus: {header.Kind}");
                WriteLine(stream, "ERR:KIND\n");
                return;
            }

            var filename = Path.GetFileName(header.FileName);
            var finalName = GetUniqueFileName(ReceiverDestination, filename);
            partPath = Path.Combine(ReceiverDestination, finalName + ".part");
            var finalPath = Path.Combine(ReceiverDestination, finalName);
            SafeRemove(partPath);

            WriteLine(stream, "OK:READY\n");

            var received = 0L;
            using var hasher = header.UseChecksum ? SHA256.Create() : null;
            var buffer = new byte[ChunkSize];
            var stopwatch = Stopwatch.StartNew();

            using (var fileStream = File.Create(partPath))
            {
                while (received < header.Size)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, header.Size - received));
                    if (read == 0)
                    {
                        break;
                    }

                    fileStream.Write(buffer, 0, read);
                    received += read;
                    hasher?.TransformBlock(buffer, 0, read, null, 0);
                    UpdateProgress(finalName, received, header.Size, stopwatch.Elapsed.TotalSeconds);
                }
            }

            hasher?.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            WriteLine(stream, "OK:RECEIVED\n");

            if (received != header.Size)
            {
                AppendLog("Hiányos fogadás, takarítás.");
                SafeRemove(partPath);
                WriteLine(stream, "ERR:LENGTH\n");
                return;
            }

            if (header.UseChecksum && hasher is not null)
            {
                var checksum = Convert.ToHexString(hasher.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
                if (!string.Equals(checksum, header.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("Ellenőrzőösszeg eltérés, takarítás.");
                    SafeRemove(partPath);
                    WriteLine(stream, "ERR:CHECKSUM\n");
                    return;
                }
            }

            FinalizeReceivedFile(partPath, finalPath);
            var elapsed = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            AppendLog($"Mentve: {finalName} ({HumanBytes(header.Size)}) {elapsed:0.00} s alatt.");
            WriteLine(stream, "OK:DONE\n");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Fogadás megszakítva.");
            if (partPath is not null)
            {
                SafeRemove(partPath);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Fogadási hiba: {ex.Message}");
            _logger.LogWarning(ex, "Fogadó kliens hiba.");
            if (partPath is not null)
            {
                SafeRemove(partPath);
            }
            try
            {
                using var stream = client.GetStream();
                WriteLine(stream, "ERR:EXC\n");
            }
            catch
            {
                // Ignore secondary failures.
            }
        }
        finally
        {
            client.Close();
            ResetProgress();
        }
    }

    private async Task SendFileAsync(string host, string path, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            AppendLog($"Hiányzó fájl: {path}");
            return;
        }

        var size = new FileInfo(path).Length;
        var fileName = Path.GetFileName(path);
        var keyHash = ComputeSha256Hex(SenderSharedKey);
        var checksum = string.Empty;

        if (UseChecksum)
        {
            AppendLog($"SHA-256 számítás: {fileName}...");
            checksum = ComputeFileSha256(path, token);
        }

        var header = new TransferHeader
        {
            Kind = "file",
            FileName = fileName,
            Size = size,
            UseChecksum = UseChecksum,
            Sha256 = checksum,
            SharedKeyHash = keyHash
        };

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, HeaderOptions);
        using var client = new TcpClient();
        client.SendTimeout = TimeoutSeconds * 1000;
        client.ReceiveTimeout = TimeoutSeconds * 1000;
        await client.ConnectAsync(host, TransferPort, token).ConfigureAwait(false);

        using var stream = client.GetStream();
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.WriteByte((byte)'\n');

        var response = ReadLine(stream, token);
        if (response is null || !Encoding.UTF8.GetString(response).StartsWith("OK:READY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A fogadó nem kész.");
        }

        var sent = 0L;
        var buffer = new byte[ChunkSize];
        var stopwatch = Stopwatch.StartNew();
        using (var file = File.OpenRead(path))
        {
            int read;
            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                stream.Write(buffer, 0, read);
                sent += read;
                UpdateProgress(fileName, sent, size, stopwatch.Elapsed.TotalSeconds);
            }
        }

        var receivedAck = ReadLine(stream, token);
        if (receivedAck is null || !Encoding.UTF8.GetString(receivedAck).StartsWith("OK:RECEIVED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A fogadó hibát jelzett.");
        }

        var doneAck = ReadLine(stream, token);
        if (doneAck is null || !Encoding.UTF8.GetString(doneAck).StartsWith("OK:DONE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A fogadó nem fejezte be.");
        }

        var elapsed = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        AppendLog($"Elküldve: {fileName} ({HumanBytes(size)}) {elapsed:0.00} s alatt.");
    }

    private List<string> PrepareSendItems(IEnumerable<string> items, List<string> tempFiles, CancellationToken token)
    {
        var output = new List<string>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(item))
            {
                var baseName = Path.GetFileName(Path.GetFullPath(item).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var zipPath = Path.Combine(GetTempDirectory(), $"{baseName}_{timestamp}.zip");

                AppendLog($"Mappa tömörítése: {item}...");
                SetIndeterminateProgress($"Tömörítés: {baseName}.zip...");
                CreateZipFromDirectory(item, zipPath, token);

                output.Add(zipPath);
                tempFiles.Add(zipPath);
                AppendLog($"ZIP kész ({HumanBytes(new FileInfo(zipPath).Length)}).");
            }
            else if (File.Exists(item))
            {
                output.Add(item);
            }
            else
            {
                AppendLog($"Nem található, kihagyva: {item}");
            }
        }

        ResetProgress();
        return output;
    }

    private void CreateZipFromDirectory(string sourceDirectory, string destinationZip, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZip) ?? ".");
        var rootParent = Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory;

        using var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var file in EnumerateFiles(sourceDirectory, token))
        {
            token.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(rootParent, file);
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }

    private IEnumerable<string> EnumerateFiles(string directory, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var subDir in Directory.EnumerateDirectories(current))
            {
                if (ShouldExcludeDirectory(Path.GetFileName(subDir)))
                {
                    continue;
                }

                stack.Push(subDir);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!ShouldExcludeFile(Path.GetFileName(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private void AutoClean()
    {
        CleanPartFilesInternal(silent: true);
        CleanTempZips();
    }

    private int CleanPartFilesInternal(bool silent)
    {
        if (string.IsNullOrWhiteSpace(ReceiverDestination) || !Directory.Exists(ReceiverDestination))
        {
            if (!silent)
            {
                AppendLog("Érvénytelen célmappa.");
            }
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(ReceiverDestination, "*.part"))
        {
            if (SafeRemove(file))
            {
                removed++;
            }
        }

        if (!silent && removed > 0)
        {
            AppendLog($"{removed} db .part fájl törölve.");
        }

        return removed;
    }

    private void CleanTempZips()
    {
        var tempDir = GetTempDirectory();
        if (!Directory.Exists(tempDir))
        {
            return;
        }

        var now = DateTime.Now;
        foreach (var file in Directory.EnumerateFiles(tempDir, "*.zip"))
        {
            try
            {
                var age = now - File.GetLastWriteTime(file);
                if (age > TempZipMaxAge)
                {
                    SafeRemove(file);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private void LoadSettings()
    {
        lock (_settingsLock)
        {
            _isLoadingSettings = true;
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return;
                }

                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<LanTransferSettings>(json, SettingsOptions);
                if (settings is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(settings.ReceiverDestination))
                {
                    ReceiverDestination = settings.ReceiverDestination;
                }

                if (!string.IsNullOrWhiteSpace(settings.SenderIp))
                {
                    SenderIp = settings.SenderIp;
                }

                UseChecksum = settings.UseChecksum;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load LAN transfer settings.");
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        lock (_settingsLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var settings = new LanTransferSettings
                {
                    ReceiverDestination = ReceiverDestination,
                    SenderIp = SenderIp,
                    UseChecksum = UseChecksum
                };

                var json = JsonSerializer.Serialize(settings, SettingsOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save LAN transfer settings.");
            }
        }
    }

    private void ScheduleSave()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateProgress(string name, long done, long total, double elapsedSeconds)
    {
        if (total <= 0)
        {
            return;
        }

        var pct = Math.Clamp(done / (double)total * 100.0, 0.0, 100.0);
        var bps = elapsedSeconds > 0 ? done / elapsedSeconds : 0;

        var speedText = bps > 0 ? $" @ {HumanBytes((long)bps)}/s" : string.Empty;
        var etaText = bps > 0 && total > done
            ? $" kb. {Math.Max(0, (int)Math.Round((total - done) / bps))}s hátra"
            : string.Empty;

        Dispatcher.UIThread.Post(() =>
        {
            IsProgressIndeterminate = false;
            ProgressValue = pct;
            ProgressLabel = $"{name}: {HumanBytes(done)} / {HumanBytes(total)} ({pct:0.0}%){speedText}{etaText}";
        });
    }

    private void SetIndeterminateProgress(string label)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsProgressIndeterminate = true;
            ProgressValue = 0;
            ProgressLabel = label;
        });
    }

    private void ResetProgress()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressLabel = "Nincs aktív átvitel.";
        });
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.UIThread.Post(() =>
        {
            LogOutput = string.IsNullOrEmpty(LogOutput)
                ? line
                : $"{LogOutput}{Environment.NewLine}{line}";
        });
    }

    private static string GetTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lan_drop_tmp");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetUniqueFileName(string destDir, string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        var counter = 1;
        var candidate = filename;

        while (File.Exists(Path.Combine(destDir, candidate))
               || File.Exists(Path.Combine(destDir, candidate + ".part")))
        {
            candidate = $"{baseName} ({counter}){ext}";
            counter++;
        }

        return candidate;
    }

    private static byte[]? ReadLine(Stream stream, CancellationToken token)
    {
        var data = new List<byte>(128);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var b = stream.ReadByte();
            if (b == -1)
            {
                return data.Count == 0 ? null : data.ToArray();
            }

            if (b == '\n')
            {
                return data.ToArray();
            }

            data.Add((byte)b);
        }
    }

    private static void WriteLine(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ComputeSha256Hex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeFileSha256(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    private static bool SafeRemove(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void FinalizeReceivedFile(string sourcePath, string destinationPath)
    {
        try
        {
            MoveWithRetry(sourcePath, destinationPath);
            return;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            AppendLog("Átnevezés sikertelen, másolással próbálkozunk...");
        }

        if (CopyWithRetry(sourcePath, destinationPath))
        {
            if (!DeleteWithRetry(sourcePath))
            {
                AppendLog("A .part fájl törlése sikertelen, kézi takarítás szükséges.");
            }

            return;
        }

        throw new IOException("Nem sikerült a fájl véglegesítése.");
    }

    private static void MoveWithRetry(string sourcePath, string destinationPath, int attempts = 15, int delayMs = 300)
    {
        Exception? lastError = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                if (i < attempts - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        throw lastError ?? new IOException("Nem sikerült a fájl átmozgatása.");
    }

    private static bool CopyWithRetry(string sourcePath, string destinationPath, int attempts = 10, int delayMs = 300)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (i < attempts - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        return false;
    }

    private static bool DeleteWithRetry(string path, int attempts = 5, int delayMs = 200)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (SafeRemove(path))
            {
                return true;
            }

            if (i < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    private static string HumanBytes(long value)
    {
        double size = value;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        foreach (var unit in units)
        {
            if (size < 1024.0)
            {
                return unit == "B" ? $"{size:0} {unit}" : $"{size:0.00} {unit}";
            }
            size /= 1024.0;
        }

        return $"{size:0.00} PB";
    }

    private static string GetLocalIpGuess()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            // ignore
        }

        return "127.0.0.1";
    }

    private static string GetDefaultDownloadPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return Path.Combine(profile, "Downloads");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static bool ShouldExcludeDirectory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".idea", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExcludeFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return lower is "thumbs.db" or ".ds_store"
               || lower.EndsWith(".part", StringComparison.Ordinal)
               || lower.EndsWith(".tmp", StringComparison.Ordinal)
               || lower.EndsWith(".temp", StringComparison.Ordinal)
               || lower.EndsWith(".log~", StringComparison.Ordinal);
    }

    private async Task TryOpenFirewallPortAsync(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0 || !IsRunningAsAdministrator())
        {
            return;
        }

        var ruleName = $"KVM Switch LAN Transfer {port}";
        var command =
            $"if (-not (Get-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue)) " +
            $"{{ New-NetFirewallRule -DisplayName '{ruleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} | Out-Null }}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start PowerShell process for firewall rule.");
                return;
            }

            _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stdErr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Ensured firewall rule for port {Port}.", port);
            }
            else
            {
                _logger.LogWarning("Firewall rule command failed (exit {ExitCode}). {Error}", process.ExitCode, stdErr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure firewall rule for port {Port}.", port);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class TransferHeader
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("filename")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("use_checksum")]
        public bool UseChecksum { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("shared_key_hash")]
        public string SharedKeyHash { get; set; } = string.Empty;
    }

    private sealed class LanTransferSettings
    {
        public string ReceiverDestination { get; set; } = string.Empty;
        public string SenderIp { get; set; } = string.Empty;
        public bool UseChecksum { get; set; }
    }
}

