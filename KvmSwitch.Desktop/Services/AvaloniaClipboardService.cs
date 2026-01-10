using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using Microsoft.Extensions.Logging;

namespace KvmSwitch.Desktop.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly IDataNetworkService _dataNetworkService;
    private readonly ILogger<AvaloniaClipboardService> _logger;
    private readonly DispatcherTimer _timer;
    private bool _isRunning;
    private bool _isUpdating;
    private string? _lastText;
    private int _lastHash;

    public event EventHandler<string>? ClipboardTextChanged;

    public AvaloniaClipboardService(
        IDataNetworkService dataNetworkService,
        ILogger<AvaloniaClipboardService> logger)
    {
        _dataNetworkService = dataNetworkService ?? throw new ArgumentNullException(nameof(dataNetworkService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;

        _dataNetworkService.MessageReceived += OnMessageReceived;
    }

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _timer.Stop();
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

        var clipboard = TryGetClipboard();
        if (clipboard == null)
        {
            return;
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

        if (!HasChanged(text))
        {
            return;
        }

        UpdateLocalState(text);
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

    private void OnMessageReceived(object? sender, (object Message, Guid ClientId) args)
    {
        if (args.Message is ClipboardMessage clipboardMessage)
        {
            ApplyRemoteText(clipboardMessage.Text);
        }
    }

    private void ApplyRemoteText(string? text)
    {
        var normalized = text ?? string.Empty;
        if (!HasChanged(normalized))
        {
            return;
        }

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

    private static IClipboard? TryGetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }
}
