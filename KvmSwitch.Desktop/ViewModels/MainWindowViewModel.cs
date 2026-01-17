using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using KvmSwitch.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpHook.Data;

namespace KvmSwitch.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public enum DeviceRole
    {
        Controller,
        InputProvider,
        Receiver
    }

    private enum InputTarget
    {
        Desktop,
        Laptop,
        EliteDesk
    }

    private const string ControllerSerialPort = "COM7";
    private const int ClipboardPort = 54545;
    private const double DefaultWindowWidth = 450;
    private const double ReceiverPanelWidth = 188;
    private const double ReceiverPanelSpacing = 12;
    private const string AutostartAppName = "KvmSwitch";
    private const int MouseSendIntervalMs = 8;
    private const int MouseSendIdleTimeoutMs = 200;
    private const KeyCode MonitorOnOffFunctionKey = KeyCode.VcF21;
    private const KeyCode Hdmi1FunctionKey = KeyCode.VcF16;
    private const KeyCode Hdmi2FunctionKey = KeyCode.VcF17;
    private const KeyCode Hang1FunctionKey = KeyCode.VcF18;
    private const KeyCode Hang2FunctionKey = KeyCode.VcF19;
    private const KeyCode Hang3FunctionKey = KeyCode.VcF20;
    private const int MonitorOnOffAltKeyCode1 = -132;
    private const int MonitorOnOffAltKeyCode2 = 108;
    private const int HotkeySuppressionWindowMs = 250;
    private static readonly Dictionary<string, KeyCode> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = KeyCode.VcLeftControl,
        ["CONTROL"] = KeyCode.VcLeftControl,
        ["ALT"] = KeyCode.VcLeftAlt,
        ["SHIFT"] = KeyCode.VcLeftShift,
        ["WIN"] = KeyCode.VcLeftMeta,
        ["META"] = KeyCode.VcLeftMeta,
        ["SUPER"] = KeyCode.VcLeftMeta,
        ["TAB"] = KeyCode.VcTab,
        ["ENTER"] = KeyCode.VcEnter,
        ["ESC"] = KeyCode.VcEscape,
        ["ESCAPE"] = KeyCode.VcEscape,
        ["SPACE"] = KeyCode.VcSpace,
        ["BACKSPACE"] = KeyCode.VcBackspace,
        ["DEL"] = KeyCode.VcDelete,
        ["DELETE"] = KeyCode.VcDelete,
        ["INSERT"] = KeyCode.VcInsert,
        ["HOME"] = KeyCode.VcHome,
        ["END"] = KeyCode.VcEnd,
        ["PAGEUP"] = KeyCode.VcPageUp,
        ["PAGEDOWN"] = KeyCode.VcPageDown,
        ["UP"] = KeyCode.VcUp,
        ["DOWN"] = KeyCode.VcDown,
        ["LEFT"] = KeyCode.VcLeft,
        ["RIGHT"] = KeyCode.VcRight
    };

    private readonly INetworkService _networkService;
    private readonly IDataNetworkService _dataNetworkService;
    private readonly IInputService _inputService;
    private readonly ISerialService _serialService;
    private readonly IMonitorControlService _monitorControlService;
    private readonly IScreenService _screenService;
    private readonly IRegistryService _registryService;
    private readonly IClipboardService _clipboardService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _isLoadingSettings;
    private bool _updatingRoleSelection;
    private bool _serialSubscribed;
    private Guid? _inputProviderId;
    private Guid? _receiverId;
    private int? _inputProviderScreenWidth;
    private int? _inputProviderScreenHeight;
    private int? _remoteScreenWidth;
    private int? _remoteScreenHeight;
    private volatile InputTarget _currentTarget = InputTarget.Desktop;
    private bool _hasConnectedOnce;
    private string? _statusErrorMessage;
    private int _controllerVirtualX;
    private int _controllerVirtualY;
    private bool _controllerVirtualInitialized;
    private int _receiverVirtualX;
    private int _receiverVirtualY;
    private bool _receiverVirtualInitialized;
    private readonly ConcurrentDictionary<Guid, (int Width, int Height)> _clientScreenSizes = new();
    private readonly object _mouseSendLock = new();
    private readonly object _hotkeySuppressionLock = new();
    private readonly Dictionary<int, long> _hotkeySuppression = new();
    private int _pendingMouseDeltaX;
    private int _pendingMouseDeltaY;
    private Guid? _pendingMouseTargetId;
    private Task? _mouseSendTask;
    private CancellationTokenSource? _mouseSendCts;

    [ObservableProperty]
    private DeviceRole selectedRole = DeviceRole.InputProvider;

    [ObservableProperty]
    private bool isControllerSelected;

    [ObservableProperty]
    private bool isInputProviderSelected;

    [ObservableProperty]
    private bool isReceiverSelected;

    [ObservableProperty]
    private int port = 65432;

    [ObservableProperty]
    private string hostMonitorCode = "17";

    [ObservableProperty]
    private string clientMonitorCode = "18";

    [ObservableProperty]
    private bool autoStartEnabled;

    [ObservableProperty]
    private bool autoStartService;

    [ObservableProperty]
    private bool startInTray;

    [ObservableProperty]
    private bool isServiceRunning;

    [ObservableProperty]
    private string statusMessage = "Service: Inactive";

    [ObservableProperty]
    private string statusLine1 = "Service: Inactive";

    [ObservableProperty]
    private string statusLine2 = string.Empty;

    [ObservableProperty]
    private string buttonText = "KVM Szolgáltatás Indítása";

    [ObservableProperty]
    private string receiverHostIp = "192.168.0.19";

    [ObservableProperty]
    private double windowWidth = DefaultWindowWidth;

    [ObservableProperty]
    private bool isMainViewVisible = true;

    [ObservableProperty]
    private bool isButtonMappingVisible;

    [ObservableProperty]
    private string logOutput = string.Empty;

    public ButtonMappingViewModel ButtonMapping { get; }

    public MainWindowViewModel(
        INetworkService networkService,
        IDataNetworkService dataNetworkService,
        IInputService inputService,
        ISerialService serialService,
        IMonitorControlService monitorControlService,
        IScreenService screenService,
        IRegistryService registryService,
        IClipboardService clipboardService,
        ISettingsService settingsService,
        ButtonMappingViewModel buttonMappingViewModel,
        ILogger<MainWindowViewModel> logger)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _dataNetworkService = dataNetworkService ?? throw new ArgumentNullException(nameof(dataNetworkService));
        _inputService = inputService ?? throw new ArgumentNullException(nameof(inputService));
        _serialService = serialService ?? throw new ArgumentNullException(nameof(serialService));
        _monitorControlService = monitorControlService ?? throw new ArgumentNullException(nameof(monitorControlService));
        _screenService = screenService ?? throw new ArgumentNullException(nameof(screenService));
        _registryService = registryService ?? throw new ArgumentNullException(nameof(registryService));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ButtonMapping = buttonMappingViewModel ?? throw new ArgumentNullException(nameof(buttonMappingViewModel));

        _networkService.MessageReceived += OnMessageReceived;
        _dataNetworkService.MessageReceived += OnDataMessageReceived;
        _networkService.ClientConnected += OnClientConnected;
        _networkService.ClientDisconnected += OnClientDisconnected;
        _inputService.InputReceived += OnInputReceived;
        _networkService.Port = Port;
        ButtonMapping.RequestClose += OnButtonMappingClose;

        SyncRoleSelection();
        UpdateStatusMessage();
        UpdateWindowWidth();
        LoadSettings();
        AutoStartEnabled = _registryService.IsAutostartEnabled(AutostartAppName) || AutoStartEnabled;
        if (AutoStartEnabled)
        {
            var executablePath = Environment.ProcessPath;
            _registryService.SetAutostartEnabled(AutostartAppName, executablePath, true);
        }
    }

    partial void OnSelectedRoleChanged(DeviceRole value)
    {
        if (_updatingRoleSelection)
        {
            return;
        }

        _updatingRoleSelection = true;
        IsControllerSelected = value == DeviceRole.Controller;
        IsInputProviderSelected = value == DeviceRole.InputProvider;
        IsReceiverSelected = value == DeviceRole.Receiver;
        _updatingRoleSelection = false;
        _controllerVirtualInitialized = false;
        _receiverVirtualInitialized = false;
        UpdateWindowWidth();
        UpdateStatusMessage();
        SaveSettings();
    }

    partial void OnIsControllerSelectedChanged(bool value)
    {
        if (_updatingRoleSelection || !value)
        {
            return;
        }

        SelectedRole = DeviceRole.Controller;
    }

    partial void OnIsInputProviderSelectedChanged(bool value)
    {
        if (_updatingRoleSelection || !value)
        {
            return;
        }

        SelectedRole = DeviceRole.InputProvider;
    }

    partial void OnIsReceiverSelectedChanged(bool value)
    {
        if (_updatingRoleSelection || !value)
        {
            return;
        }

        SelectedRole = DeviceRole.Receiver;
    }

    partial void OnPortChanged(int value)
    {
        _networkService.Port = value;
        SaveSettings();
    }

    partial void OnIsServiceRunningChanged(bool value)
    {
        ButtonText = value
            ? "KVM Szolgáltatás Leállítása"
            : "KVM Szolgáltatás Indítása";

        if (!value)
        {
            _hasConnectedOnce = false;
        }
        UpdateStatusMessage();
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        _registryService.SetAutostartEnabled(AutostartAppName, executablePath, value);
        if (value)
        {
            StartInTray = true;
            AutoStartService = true;
        }

        SaveSettings();
    }

    partial void OnAutoStartServiceChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnStartInTrayChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnHostMonitorCodeChanged(string value)
    {
        SaveSettings();
    }

    partial void OnClientMonitorCodeChanged(string value)
    {
        SaveSettings();
    }

    partial void OnReceiverHostIpChanged(string value)
    {
        SaveSettings();
    }

    [RelayCommand]
    private async Task ToggleServiceAsync()
    {
        if (IsServiceRunning)
        {
            StopServices();
            return;
        }
        await StartServiceAsync().ConfigureAwait(false);
    }

    public async Task InitializeAsync()
    {
        if (AutoStartService && !IsServiceRunning)
        {
            await StartServiceAsync(fromStartup: true).ConfigureAwait(false);
        }
    }

    private async Task StartServiceAsync(bool fromStartup = false)
    {
        ClearStatusError();
        SetIsServiceRunning(true);
        AppendLog($"Service starting as {SelectedRole}...");
        _logger.LogInformation("Starting KVM service as {Role}.", SelectedRole);

        try
        {
            _networkService.Port = Port;
            if (SelectedRole == DeviceRole.Controller)
            {
                SubscribeSerial();
                _serialService.Start(ControllerSerialPort);
                await _networkService.StartServerAsync().ConfigureAwait(false);
                await _dataNetworkService.StartServerAsync().ConfigureAwait(false);
                await TryOpenFirewallPortAsync(Port).ConfigureAwait(false);
                await TryOpenFirewallPortAsync(ClipboardPort).ConfigureAwait(false);
                TryStartHotkeyCapture();
            }
            else
            {
                if (SelectedRole == DeviceRole.Receiver)
                {
                    await TryDeprioritizeVpnAsync().ConfigureAwait(false);
                }

                var hostAddress = ReceiverHostIp?.Trim();
                if (string.IsNullOrWhiteSpace(hostAddress))
                {
                    hostAddress = null;
                }

                await _networkService.StartClientAsync(hostAddress).ConfigureAwait(false);
                await _dataNetworkService.StartClientAsync(hostAddress).ConfigureAwait(false);
            }

            _clipboardService.Start();
            AutoStartService = true;
            if (fromStartup)
            {
                StartInTray = true;
            }

            AppendLog("Service started.");
            ClearStatusError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start KVM service.");
            AppendLog("Error: Failed to start service.");
            SetStatusError("Failed to start service.");
            SetIsServiceRunning(false);
        }
    }

    [RelayCommand]
    private void ShowWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is Window window)
            {
                App.ShowWindowCentered(window);
            }
        }
    }

    [RelayCommand]
    private void ExitApplication()
    {
        Environment.Exit(0);
    }

    [RelayCommand]
    private void OpenFileTransfer()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var mainWindow = desktop.MainWindow;
        if (mainWindow is null)
        {
            return;
        }

        FileTransferWindow? existing = null;
        foreach (var window in desktop.Windows)
        {
            if (window is FileTransferWindow fileTransfer)
            {
                existing = fileTransfer;
                break;
            }
        }

        if (existing is not null)
        {
            mainWindow.Hide();
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var newWindow = App.Services.GetRequiredService<FileTransferWindow>();
        mainWindow.Hide();
        newWindow.Show();
        newWindow.WindowState = WindowState.Normal;
        newWindow.Activate();
    }

    [RelayCommand]
    private void OpenButtonMapping()
    {
        IsMainViewVisible = false;
        IsButtonMappingVisible = true;
    }

    private void OnButtonMappingClose()
    {
        IsButtonMappingVisible = false;
        IsMainViewVisible = true;
    }

    private void StopServices()
    {
        _ = StopServicesAsync();
    }

    public Task ShutdownAsync()
    {
        return StopServicesAsync();
    }

    private Task StopServicesAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                StopMouseSendLoop();
                _clipboardService.Stop();
                _dataNetworkService.Stop();
                _networkService.Stop();
                _inputService.Stop();
                _serialService.Stop();
                UnsubscribeSerial();
                SetIsServiceRunning(false);
                AppendLog("Service stopped.");
                _logger.LogInformation("KVM service stopped.");
                ClearStatusError();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop KVM service.");
                AppendLog("Error: Failed to stop service.");
                SetStatusError("Failed to stop service.");
            }
        });
    }

    private void OnMessageReceived(object? sender, (object Message, Guid ClientId) args)
    {
        var message = args.Message;
        var clientId = args.ClientId;

        if (message is string textMessage)
        {
            if (TryHandleScreenSizeMessage(clientId, textMessage))
            {
                return;
            }
        }

        if (message is ClientRoleMessage roleMessage)
        {
            if (SelectedRole == DeviceRole.Controller)
            {
                RegisterClientRole(clientId, roleMessage);
            }

            return;
        }

        if (message is ControlMessage controlMessage)
        {
            if (SelectedRole == DeviceRole.InputProvider)
            {
                try
                {
                    if (controlMessage.Command == ControlCommand.StartStreaming)
                    {
                        var (width, height) = GetLocalScreenBounds();
                        if (width > 0 && height > 0)
                        {
                            _inputService.SetPointerBounds(width, height);
                        }

                        _inputService.Start();
                    }
                    else if (controlMessage.Command == ControlCommand.StopStreaming)
                    {
                        _inputService.Stop();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to handle control command {Command}.", controlMessage.Command);
                    AppendLog("Error: Failed to handle control command.");
                    SetStatusError("Failed to handle control command.");
                }
            }

            return;
        }

        if (message is MappedButtonMessage mappedButtonMessage)
        {
            if (SelectedRole == DeviceRole.Receiver || SelectedRole == DeviceRole.InputProvider)
            {
                TryExecuteMappedAction(mappedButtonMessage.Action);
            }

            return;
        }

        if (SelectedRole == DeviceRole.Controller && message is InputEvent inputEvent)
        {
            if (_currentTarget == InputTarget.Laptop && _receiverId is not null)
            {
                if (inputEvent.EventType == InputEventType.MouseMove && inputEvent.IsRelative)
                {
                    var (localWidth, localHeight) = GetLocalScreenBounds();
                    var (scaledX, scaledY) = ScaleRelativeDelta(
                        inputEvent.MouseX,
                        inputEvent.MouseY,
                        _inputProviderScreenWidth,
                        _inputProviderScreenHeight,
                        localWidth,
                        localHeight);

                    QueueRelativeMouseMoveForReceiver(scaledX, scaledY, _receiverId.Value);
                }
                else
                {
                    _ = _networkService.SendAsync(inputEvent, _receiverId);
                }

                return;
            }

            if (_currentTarget == InputTarget.EliteDesk)
            {
                try
                {
                    if (inputEvent.EventType == InputEventType.MouseMove && inputEvent.IsRelative)
                    {
                        var (width, height) = GetLocalScreenBounds();
                        var (scaledX, scaledY) = ScaleRelativeDelta(
                            inputEvent.MouseX,
                            inputEvent.MouseY,
                            _inputProviderScreenWidth,
                            _inputProviderScreenHeight,
                            width,
                            height);

                        var (x, y) = ApplyRelativeDelta(
                            ref _controllerVirtualX,
                            ref _controllerVirtualY,
                            ref _controllerVirtualInitialized,
                            scaledX,
                            scaledY,
                            width,
                            height);

                        var absoluteEvent = inputEvent with { MouseX = x, MouseY = y, IsRelative = false };
                        _inputService.SimulateInput(absoluteEvent);
                        return;
                    }

                    var adjustedEvent = AdjustMouseCoordinatesForLocal(inputEvent);
                    _inputService.SimulateInput(adjustedEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to simulate input for EliteDesk.");
                }
            }

            return;
        }

        if (message is not InputEvent receivedInput)
        {
            AppendLog("Msg received!");
            _logger.LogInformation("Message received: {MessageType}.", message?.GetType().Name ?? "null");
            return;
        }

        if (SelectedRole == DeviceRole.Receiver)
        {
            try
            {
                if (receivedInput.EventType == InputEventType.MouseMove && receivedInput.IsRelative)
                {
                    var (width, height) = GetLocalScreenBounds();
                    var (scaledX, scaledY) = ScaleRelativeDelta(
                        receivedInput.MouseX,
                        receivedInput.MouseY,
                        _remoteScreenWidth,
                        _remoteScreenHeight,
                        width,
                        height);
                    var (x, y) = ApplyRelativeDelta(
                        ref _receiverVirtualX,
                        ref _receiverVirtualY,
                        ref _receiverVirtualInitialized,
                        scaledX,
                        scaledY,
                        width,
                        height);

                    var absoluteEvent = receivedInput with { MouseX = x, MouseY = y, IsRelative = false };
                    _inputService.SimulateInput(absoluteEvent);
                }
                else
                {
                    _inputService.SimulateInput(receivedInput);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to simulate input event.");
            }
        }
    }

    private void OnDataMessageReceived(object? sender, (object Message, Guid ClientId) args)
    {
        if (SelectedRole != DeviceRole.Controller || !_dataNetworkService.IsServer)
        {
            return;
        }

        if (args.Message is ClipboardMessage clipboardMessage)
        {
            _ = _dataNetworkService.SendAsync(clipboardMessage);
        }
    }

    private void OnInputReceived(object? sender, InputEvent inputEvent)
    {
        if (SelectedRole == DeviceRole.Controller && TryHandleHotkeyInput(inputEvent))
        {
            return;
        }

        if (SelectedRole != DeviceRole.InputProvider || !_networkService.IsConnected)
        {
            return;
        }

        _ = SendInputAsync(inputEvent);
    }

    private void TryStartHotkeyCapture()
    {
        try
        {
            _inputService.Start(suppressLocalInput: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start hotkey capture.");
            AppendLog("Error: Failed to start hotkey capture.");
            SetStatusError("Failed to start hotkey capture.");
        }
    }

    private bool TryHandleHotkeyInput(InputEvent inputEvent)
    {
        if (inputEvent.EventType != InputEventType.KeyDown)
        {
            return false;
        }

        if (IsHotkeySuppressed(inputEvent.Key))
        {
            return false;
        }

        if (inputEvent.Key == (int)MonitorOnOffFunctionKey
            || inputEvent.Key == MonitorOnOffAltKeyCode1
            || inputEvent.Key == MonitorOnOffAltKeyCode2)
        {
            _ = ToggleMonitorPowerAsync();
            LogCommandAction("Hotkey: Monitor Power Toggle");
            return true;
        }

        if (inputEvent.Key == (int)Hdmi1FunctionKey)
        {
            _ = SwitchMonitorAsync(HostMonitorCode);
            LogCommandAction("Hotkey: HDMI 1");
            return true;
        }

        if (inputEvent.Key == (int)Hdmi2FunctionKey)
        {
            _ = SwitchMonitorAsync(ClientMonitorCode);
            LogCommandAction("Hotkey: HDMI 2");
            return true;
        }

        return false;
    }

    private bool IsHotkeySuppressed(int keyCode)
    {
        var now = Environment.TickCount64;
        lock (_hotkeySuppressionLock)
        {
            if (!_hotkeySuppression.TryGetValue(keyCode, out var expiresAt))
            {
                return false;
            }

            if (expiresAt < now)
            {
                _hotkeySuppression.Remove(keyCode);
                return false;
            }

            return true;
        }
    }

    private void OnClientConnected(object? sender, Guid clientId)
    {
        _hasConnectedOnce = true;
        UpdateStatusMessage();

        _ = SendScreenSizeMessageAsync(SelectedRole == DeviceRole.Controller ? clientId : null);

        if (SelectedRole == DeviceRole.Controller)
        {
            AppendLog($"Client connected: {clientId}. Waiting for role handshake.");
            _logger.LogInformation("Client connected: {ClientId}. Waiting for role handshake.", clientId);
            return;
        }

        _ = SendRoleHandshakeAsync();
    }

    private void OnClientDisconnected(object? sender, Guid clientId)
    {
        _clientScreenSizes.TryRemove(clientId, out _);

        if (SelectedRole == DeviceRole.Controller)
        {
            if (_inputProviderId == clientId)
            {
                _inputProviderId = null;
                _inputProviderScreenWidth = null;
                _inputProviderScreenHeight = null;
                _controllerVirtualInitialized = false;
                ResetPendingMouse();
                AppendLog("Input Provider disconnected.");
                _logger.LogInformation("Input Provider disconnected: {ClientId}.", clientId);
                UpdateStatusMessage();
                return;
            }

            if (_receiverId == clientId)
            {
                _receiverId = null;
                ResetPendingMouse();
                AppendLog("Receiver disconnected.");
                _logger.LogInformation("Receiver disconnected: {ClientId}.", clientId);
                UpdateStatusMessage();
                return;
            }
        }
        else
        {
            _remoteScreenWidth = null;
            _remoteScreenHeight = null;
        }

        UpdateStatusMessage();
    }

    private async Task SendRoleHandshakeAsync()
    {
        var role = SelectedRole switch
        {
            DeviceRole.InputProvider => ClientRole.InputProvider,
            DeviceRole.Receiver => ClientRole.Receiver,
            _ => (ClientRole?)null
        };

        if (role is null)
        {
            return;
        }

        try
        {
            _logger.LogDebug("Preparing role handshake for {Role}. ThreadId={ThreadId}.", role.Value, Environment.CurrentManagedThreadId);
            var message = new ClientRoleMessage { Role = role.Value };

            var (width, height) = await Dispatcher.UIThread
                .InvokeAsync(() => _screenService.GetPrimaryScreenSize());
            if (width > 0 && height > 0)
            {
                message.ScreenWidth = (int)Math.Round(width);
                message.ScreenHeight = (int)Math.Round(height);
            }

            await _networkService.SendAsync(message).ConfigureAwait(false);
            _logger.LogInformation("Role handshake sent: {Role}.", role.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send role handshake for {Role}.", role.Value);
        }
    }

    private async Task SendScreenSizeMessageAsync(Guid? targetClientId)
    {
        try
        {
            _logger.LogDebug("Preparing screen size message for {ClientId}. ThreadId={ThreadId}.", targetClientId, Environment.CurrentManagedThreadId);
            var (width, height) = await Dispatcher.UIThread
                .InvokeAsync(() => _screenService.GetPrimaryScreenSize());
            var widthValue = (int)Math.Round(width);
            var heightValue = (int)Math.Round(height);
            if (widthValue <= 0 || heightValue <= 0)
            {
                _logger.LogDebug("Screen size unavailable (width={Width} height={Height}) for {ClientId}.", widthValue, heightValue, targetClientId);
                return;
            }

            var message = $"ScreenSize|{widthValue}|{heightValue}";
            await _networkService.SendAsync(message, targetClientId).ConfigureAwait(false);
            _logger.LogDebug("Screen size message sent to {ClientId} ({Width}x{Height}).", targetClientId, widthValue, heightValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send screen size message to {ClientId}.", targetClientId);
        }
    }

    private bool TryHandleScreenSizeMessage(Guid clientId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (!message.StartsWith("ScreenSize|", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = message.Split('|');
        if (parts.Length < 3)
        {
            return true;
        }

        if (!int.TryParse(parts[1], out var width) || !int.TryParse(parts[2], out var height))
        {
            return true;
        }

        if (width <= 0 || height <= 0)
        {
            return true;
        }

        if (SelectedRole == DeviceRole.Controller)
        {
            _clientScreenSizes[clientId] = (width, height);

            if (_inputProviderId == clientId)
            {
                _inputProviderScreenWidth = width;
                _inputProviderScreenHeight = height;
            }

        }
        else
        {
            _remoteScreenWidth = width;
            _remoteScreenHeight = height;
        }

        return true;
    }

    private void RegisterClientRole(Guid clientId, ClientRoleMessage message)
    {
        switch (message.Role)
        {
            case ClientRole.InputProvider:
                if (_inputProviderId == clientId)
                {
                    return;
                }

                if (_inputProviderId != null && _inputProviderId != clientId)
                {
                    AppendLog($"Input Provider reassigned: {_inputProviderId} -> {clientId}");
                    _logger.LogWarning("Input Provider reassigned: {OldClientId} -> {ClientId}.", _inputProviderId, clientId);
                }

                _inputProviderId = clientId;
                var inputProviderWidth = message.ScreenWidth;
                var inputProviderHeight = message.ScreenHeight;
                if (_clientScreenSizes.TryGetValue(clientId, out var inputProviderSize))
                {
                    inputProviderWidth = inputProviderSize.Width;
                    inputProviderHeight = inputProviderSize.Height;
                }

                _inputProviderScreenWidth = inputProviderWidth;
                _inputProviderScreenHeight = inputProviderHeight;
                AppendLog("Input Provider registered.");
                _logger.LogInformation("Input Provider registered: {ClientId}.", clientId);
                break;
            case ClientRole.Receiver:
                if (_receiverId == clientId)
                {
                    return;
                }

                if (_receiverId != null && _receiverId != clientId)
                {
                    AppendLog($"Receiver reassigned: {_receiverId} -> {clientId}");
                    _logger.LogWarning("Receiver reassigned: {OldClientId} -> {ClientId}.", _receiverId, clientId);
                }

                _receiverId = clientId;
                AppendLog("Receiver registered.");
                _logger.LogInformation("Receiver registered: {ClientId}.", clientId);
                break;
        }
    }

    private void OnCommandReceived(object? sender, string command)
    {
        _ = HandleCommandAsync(command);
    }

    private void OnRawButtonReceived(object? sender, string buttonId)
    {
        if (SelectedRole != DeviceRole.Controller)
        {
            return;
        }

        if (ButtonMapping.IsListening)
        {
            return;
        }

        if (!ButtonMapping.TryGetMapping(buttonId, out var mapping))
        {
            return;
        }

        _ = HandleMappedButtonAsync(mapping);
    }

    private async Task HandleMappedButtonAsync(ButtonMapping mapping)
    {
        if (mapping == null || string.IsNullOrWhiteSpace(mapping.Action))
        {
            return;
        }

        if (mapping.Target == ButtonMappingTarget.Local)
        {
            TryExecuteMappedAction(mapping.Action);
            return;
        }

        if (mapping.Target == ButtonMappingTarget.InputProvider)
        {
            if (_inputProviderId is null)
            {
                AppendLog("Mapped button ignored: Input Provider not connected.");
                _logger.LogWarning("Mapped button ignored because Input Provider is not connected.");
                return;
            }

            await SendMappedButtonToClientAsync(_inputProviderId.Value, mapping.Action).ConfigureAwait(false);
            return;
        }

        if (_receiverId is null)
        {
            AppendLog("Mapped button ignored: Receiver not connected.");
            _logger.LogWarning("Mapped button ignored because receiver is not connected.");
            return;
        }

        await SendMappedButtonToClientAsync(_receiverId.Value, mapping.Action).ConfigureAwait(false);
    }

    private async Task SendMappedButtonToClientAsync(Guid clientId, string action)
    {
        try
        {
            var message = new MappedButtonMessage { Action = action };
            await _networkService.SendAsync(message, clientId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send mapped button action.");
            AppendLog("Error: Failed to send mapped button action.");
            SetStatusError("Failed to send mapped button action.");
        }
    }

    private async Task SendInputAsync(InputEvent inputEvent)
    {
        try
        {
            await _networkService.SendAsync(inputEvent, targetClientId: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send input event.");
            AppendLog("Error: Failed to send input event.");
            SetStatusError("Failed to send input event.");
        }
    }

    private async Task SendControlMessageAsync(ControlCommand command)
    {
        try
        {
            var message = new ControlMessage { Command = command };
            await _networkService.SendAsync(message, targetClientId: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send control command {Command}.", command);
            AppendLog("Error: Failed to send control command.");
            SetStatusError("Failed to send control command.");
        }
    }

    private async Task HandleCommandAsync(string command)
    {
        if (SelectedRole != DeviceRole.Controller)
        {
            return;
        }

        if (string.Equals(command, "KEY_Asztal", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanSwitchLocalTarget("Desktop"))
            {
                return;
            }

            SetCurrentTarget(InputTarget.Desktop);
            await SwitchMonitorAsync(HostMonitorCode).ConfigureAwait(false);
            await SendControlMessageAsync(ControlCommand.StopStreaming).ConfigureAwait(false);
            LogCommandAction("Switched to Desktop");
            return;
        }

        if (string.Equals(command, "KEY_Laptop", StringComparison.OrdinalIgnoreCase))
        {
            SetCurrentTarget(InputTarget.Laptop);
            await SendControlMessageAsync(ControlCommand.StartStreaming).ConfigureAwait(false);
            LogCommandAction("Switched to Laptop");
            return;
        }

        if (string.Equals(command, "KEY_EliteDesk", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanSwitchLocalTarget("EliteDesk"))
            {
                return;
            }

            SetCurrentTarget(InputTarget.EliteDesk);
            await SwitchMonitorAsync(ClientMonitorCode).ConfigureAwait(false);
            await SendControlMessageAsync(ControlCommand.StartStreaming).ConfigureAwait(false);
            LogCommandAction("Switched to EliteDesk");
            return;
        }

        if (string.Equals(command, "KEY_HDMI_1", StringComparison.OrdinalIgnoreCase))
        {
            await SwitchMonitorAsync(HostMonitorCode).ConfigureAwait(false);
            PressFunctionKey(Hdmi1FunctionKey, "HDMI 1");
            LogCommandAction("Manual override: HDMI 1");
            return;
        }

        if (string.Equals(command, "KEY_HDMI_2", StringComparison.OrdinalIgnoreCase))
        {
            await SwitchMonitorAsync(ClientMonitorCode).ConfigureAwait(false);
            PressFunctionKey(Hdmi2FunctionKey, "HDMI 2");
            LogCommandAction("Manual override: HDMI 2");
            return;
        }

        if (string.Equals(command, "KEY_Monitor_OnOff", StringComparison.OrdinalIgnoreCase))
        {
            await ToggleMonitorPowerAsync().ConfigureAwait(false);
            PressFunctionKey(MonitorOnOffFunctionKey, "Monitor On/Off");
            LogCommandAction("Monitor Power Toggle");
            return;
        }

        if (string.Equals(command, "KEY_Hang_1", StringComparison.OrdinalIgnoreCase))
        {
            PressFunctionKey(Hang1FunctionKey, "Hang 1");
            LogCommandAction("Audio 1 key pressed (Placeholder)");
            return;
        }

        if (string.Equals(command, "KEY_Hang_2", StringComparison.OrdinalIgnoreCase))
        {
            PressFunctionKey(Hang2FunctionKey, "Hang 2");
            LogCommandAction("Audio 2 key pressed (Placeholder)");
            return;
        }

        if (string.Equals(command, "KEY_Hang_3", StringComparison.OrdinalIgnoreCase))
        {
            PressFunctionKey(Hang3FunctionKey, "Hang 3");
            LogCommandAction("Audio 3 key pressed (Placeholder)");
            return;
        }
    }

    private bool CanSwitchLocalTarget(string targetName)
    {
        if (_inputProviderId is not null)
        {
            return true;
        }

        var message = $"Input Provider not connected. Ignoring switch to {targetName}.";
        AppendLog(message);
        _logger.LogWarning(message);
        return false;
    }

    private async Task SwitchMonitorAsync(string codeValue)
    {
        if (!TryParseMonitorCode(codeValue, out var code))
        {
            AppendLog($"Invalid monitor code: {codeValue}");
            _logger.LogWarning("Invalid monitor code: {CodeValue}.", codeValue);
            SetStatusError($"Invalid monitor code {codeValue}.");
            return;
        }

        try
        {
            await _monitorControlService.SwitchInputAsync(code).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch monitor input.");
            AppendLog("Error: Failed to switch monitor input.");
            SetStatusError("Failed to switch monitor input.");
        }
    }

    private async Task ToggleMonitorPowerAsync()
    {
        try
        {
            await _monitorControlService.TogglePowerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle monitor power.");
            AppendLog("Error: Failed to toggle monitor power.");
            SetStatusError("Failed to toggle monitor power.");
        }
    }

    private void PressFunctionKey(KeyCode keyCode, string label)
    {
        try
        {
            SuppressHotkey((int)keyCode);
            _inputService.SimulateInput(new InputEvent
            {
                EventType = InputEventType.KeyDown,
                Key = (int)keyCode
            });
            _inputService.SimulateInput(new InputEvent
            {
                EventType = InputEventType.KeyUp,
                Key = (int)keyCode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to simulate {Label} key {KeyCode}.", label, keyCode);
            AppendLog($"Error: Failed to simulate {label} key.");
            SetStatusError($"Failed to simulate {label} key.");
        }
    }

    private void TryExecuteMappedAction(string action)
    {
        if (!TryParseAction(action, out var chords))
        {
            AppendLog($"Invalid mapped action: {action}");
            _logger.LogWarning("Invalid mapped action: {Action}.", action);
            return;
        }

        try
        {
            foreach (var chord in chords)
            {
                foreach (var keyCode in chord)
                {
                    SuppressHotkey(keyCode);
                    SimulateKey(keyCode, isDown: true);
                }

                for (var i = chord.Count - 1; i >= 0; i--)
                {
                    SimulateKey(chord[i], isDown: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute mapped action.");
            AppendLog("Error: Failed to execute mapped action.");
            SetStatusError("Failed to execute mapped action.");
        }
    }

    private void SimulateKey(int keyCode, bool isDown)
    {
        _inputService.SimulateInput(new InputEvent
        {
            EventType = isDown ? InputEventType.KeyDown : InputEventType.KeyUp,
            Key = keyCode
        });
    }

    private static bool TryParseAction(string action, out List<IReadOnlyList<int>> chords)
    {
        chords = new List<IReadOnlyList<int>>();
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        var segments = action.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var tokens = segment.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var chord = new List<int>();
            foreach (var token in tokens)
            {
                if (!TryParseKeyToken(token, out var keyCode))
                {
                    return false;
                }

                chord.Add(keyCode);
            }

            if (chord.Count > 0)
            {
                chords.Add(chord);
            }
        }

        return chords.Count > 0;
    }

    private static bool TryParseKeyToken(string token, out int keyCode)
    {
        keyCode = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (int.TryParse(trimmed, out var numeric))
        {
            keyCode = numeric;
            return true;
        }

        if (KeyAliases.TryGetValue(trimmed, out var alias))
        {
            keyCode = (int)alias;
            return true;
        }

        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];
            if (char.IsLetter(ch))
            {
                var name = $"Vc{char.ToUpperInvariant(ch)}";
                if (Enum.TryParse(name, out KeyCode letterKey))
                {
                    keyCode = (int)letterKey;
                    return true;
                }
            }
            else if (char.IsDigit(ch))
            {
                var name = $"Vc{ch}";
                if (Enum.TryParse(name, out KeyCode digitKey))
                {
                    keyCode = (int)digitKey;
                    return true;
                }
            }
        }

        if (trimmed.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > 1
            && int.TryParse(trimmed[1..], out var functionNumber))
        {
            var name = $"VcF{functionNumber}";
            if (Enum.TryParse(name, out KeyCode functionKey))
            {
                keyCode = (int)functionKey;
                return true;
            }
        }

        if (Enum.TryParse(trimmed, true, out KeyCode parsed))
        {
            keyCode = (int)parsed;
            return true;
        }

        return false;
    }

    private void SuppressHotkey(int keyCode)
    {
        var expiresAt = Environment.TickCount64 + HotkeySuppressionWindowMs;
        lock (_hotkeySuppressionLock)
        {
            _hotkeySuppression[keyCode] = expiresAt;
        }
    }

    private void LogCommandAction(string message)
    {
        Dispatcher.UIThread.Post(() => AppendLog(message));
        _logger.LogInformation(message);
    }

    private static bool TryParseMonitorCode(string value, out int code)
    {
        if (int.TryParse(value, out code))
        {
            return true;
        }

        code = 0;
        return false;
    }

    private void SubscribeSerial()
    {
        if (_serialSubscribed)
        {
            return;
        }

        _serialService.CommandReceived += OnCommandReceived;
        _serialService.RawButtonReceived += OnRawButtonReceived;
        _serialSubscribed = true;
    }

    private void UnsubscribeSerial()
    {
        if (!_serialSubscribed)
        {
            return;
        }

        _serialService.CommandReceived -= OnCommandReceived;
        _serialService.RawButtonReceived -= OnRawButtonReceived;
        _serialSubscribed = false;
    }

    private void AppendLog(string line)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            LogOutput = string.IsNullOrEmpty(LogOutput)
                ? line
                : $"{LogOutput}{Environment.NewLine}{line}";
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
        }
    }

    private void SetIsServiceRunning(bool value)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsServiceRunning = value;
        }
        else
        {
            Dispatcher.UIThread.Post(() => IsServiceRunning = value);
        }
    }

    private void SyncRoleSelection()
    {
        _updatingRoleSelection = true;
        IsControllerSelected = SelectedRole == DeviceRole.Controller;
        IsInputProviderSelected = SelectedRole == DeviceRole.InputProvider;
        IsReceiverSelected = SelectedRole == DeviceRole.Receiver;
        _updatingRoleSelection = false;
    }

    private void UpdateWindowWidth()
    {
        WindowWidth = IsReceiverSelected
            ? DefaultWindowWidth + ReceiverPanelWidth + ReceiverPanelSpacing
            : DefaultWindowWidth;
    }

    private void SetCurrentTarget(InputTarget target)
    {
        if (_currentTarget == target)
        {
            return;
        }

        _currentTarget = target;
        ResetPendingMouse();
        UpdateStatusMessage();
    }

    private void UpdateStatusMessage()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateStatusMessage);
            return;
        }

        var line1 = IsServiceRunning ? "Service: Active" : "Service: Inactive";
        var connectionSummary = string.Empty;

        if (IsServiceRunning)
        {
            if (_networkService.IsConnected)
            {
                connectionSummary = "Connection: Connected";

                if (SelectedRole == DeviceRole.Controller)
                {
                    connectionSummary = $"{connectionSummary} | Target: {FormatTarget(_currentTarget)}";
                }
            }
            else if (!_networkService.IsServer && _hasConnectedOnce)
            {
                connectionSummary = "Connection: Reconnecting...";
            }
            else
            {
                connectionSummary = "Connection: Not connected";
            }
        }

        if (!string.IsNullOrEmpty(connectionSummary))
        {
            line1 = $"{line1} | {connectionSummary}";
        }

        var line2 = string.IsNullOrWhiteSpace(_statusErrorMessage)
            ? string.Empty
            : $"Error: {_statusErrorMessage}";

        StatusLine1 = line1;
        StatusLine2 = line2;
        StatusMessage = string.IsNullOrEmpty(line2)
            ? line1
            : $"{line1}{Environment.NewLine}{line2}";
    }

    private void SetStatusError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatusError(message));
            return;
        }

        _statusErrorMessage = message;
        UpdateStatusMessage();
    }

    private void ClearStatusError()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ClearStatusError);
            return;
        }

        if (string.IsNullOrWhiteSpace(_statusErrorMessage))
        {
            return;
        }

        _statusErrorMessage = null;
        UpdateStatusMessage();
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var settings = _settingsService.Load();
            if (settings.Port > 0)
            {
                Port = settings.Port;
            }

            if (!string.IsNullOrWhiteSpace(settings.HostMonitorCode))
            {
                HostMonitorCode = settings.HostMonitorCode;
            }

            if (!string.IsNullOrWhiteSpace(settings.ClientMonitorCode))
            {
                ClientMonitorCode = settings.ClientMonitorCode;
            }

            if (!string.IsNullOrWhiteSpace(settings.ReceiverHostIp))
            {
                ReceiverHostIp = settings.ReceiverHostIp;
            }

            if (Enum.IsDefined(typeof(DeviceRole), settings.SelectedRole))
            {
                SelectedRole = (DeviceRole)settings.SelectedRole;
            }

            AutoStartEnabled = settings.AutoStartEnabled;
            AutoStartService = settings.AutoStartService;
            StartInTray = settings.StartInTray;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var settings = _settingsService.Load();
        settings.Port = Port;
        settings.HostMonitorCode = HostMonitorCode ?? string.Empty;
        settings.ClientMonitorCode = ClientMonitorCode ?? string.Empty;
        settings.AutoStartEnabled = AutoStartEnabled;
        settings.ReceiverHostIp = ReceiverHostIp ?? string.Empty;
        settings.SelectedRole = (int)SelectedRole;
        settings.AutoStartService = AutoStartService;
        settings.StartInTray = StartInTray;
        settings.ButtonMappings ??= new List<ButtonMapping>();

        _settingsService.Save(settings);
    }

    private async Task TryOpenFirewallPortAsync(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0 || !IsRunningAsAdministrator())
        {
            return;
        }

        var ruleName = $"KVM Switch Port {port}";
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

    private async Task TryDeprioritizeVpnAsync()
    {
        if (!OperatingSystem.IsWindows() || !IsRunningAsAdministrator())
        {
            return;
        }

        const int vpnMetric = 500;
        const int wifiMetric = 10;
        var command =
            "$vpn = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -match 'NordLynx' }; " +
            "if ($vpn) { $vpn | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -InterfaceMetric " + vpnMetric + " } }; " +
            "$wifi = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -match 'Wi-?Fi' }; " +
            "if ($wifi) { $wifi | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -InterfaceMetric " + wifiMetric + " } }";

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
                _logger.LogWarning("Failed to start PowerShell process for VPN deprioritization.");
                return;
            }

            _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stdErr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Adjusted network interface metrics for Wi-Fi/VPN.");
            }
            else
            {
                _logger.LogWarning("VPN deprioritization command failed (exit {ExitCode}). {Error}", process.ExitCode, stdErr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to adjust network interface metrics.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string FormatTarget(InputTarget target)
    {
        return target switch
        {
            InputTarget.Desktop => "Desktop",
            InputTarget.Laptop => "Laptop",
            InputTarget.EliteDesk => "EliteDesk",
            _ => target.ToString()
        };
    }

    private static bool IsMouseEvent(InputEvent inputEvent)
    {
        return inputEvent.EventType == InputEventType.MouseMove
            || inputEvent.EventType == InputEventType.MouseDown
            || inputEvent.EventType == InputEventType.MouseUp
            || inputEvent.EventType == InputEventType.MouseWheel;
    }

    private static bool IsMouseMove(InputEvent inputEvent)
    {
        return inputEvent.EventType == InputEventType.MouseMove;
    }

    private static string FormatInputEvent(InputEvent inputEvent)
    {
        return inputEvent.EventType switch
        {
            InputEventType.MouseMove => $"MouseMove x={inputEvent.MouseX} y={inputEvent.MouseY}",
            InputEventType.MouseDown => $"MouseDown button={inputEvent.MouseButton}",
            InputEventType.MouseUp => $"MouseUp button={inputEvent.MouseButton}",
            InputEventType.MouseWheel => $"MouseWheel delta={inputEvent.MouseY}",
            InputEventType.KeyDown => $"KeyDown key={inputEvent.Key}",
            InputEventType.KeyUp => $"KeyUp key={inputEvent.Key}",
            _ => inputEvent.EventType.ToString()
        };
    }

    private InputEvent AdjustMouseCoordinatesForLocal(InputEvent inputEvent)
    {
        if (inputEvent.EventType != InputEventType.MouseMove
            && inputEvent.EventType != InputEventType.MouseDown
            && inputEvent.EventType != InputEventType.MouseUp)
        {
            return inputEvent;
        }

        if (_inputProviderScreenWidth is null || _inputProviderScreenHeight is null)
        {
            return inputEvent;
        }

        var sourceWidth = _inputProviderScreenWidth.Value;
        var sourceHeight = _inputProviderScreenHeight.Value;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return inputEvent;
        }

        var (targetWidth, targetHeight) = GetLocalScreenBounds();
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return inputEvent;
        }

        var scaledX = (int)Math.Round(inputEvent.MouseX * (targetWidth / (double)sourceWidth));
        var scaledY = (int)Math.Round(inputEvent.MouseY * (targetHeight / (double)sourceHeight));

        scaledX = Math.Clamp(scaledX, 0, targetWidth - 1);
        scaledY = Math.Clamp(scaledY, 0, targetHeight - 1);

        return inputEvent with { MouseX = scaledX, MouseY = scaledY };
    }

    private (int Width, int Height) GetLocalScreenBounds()
    {
        var (width, height) = _screenService.GetPrimaryScreenSize();
        return ((int)Math.Round(width), (int)Math.Round(height));
    }

    private static (int X, int Y) ScaleRelativeDelta(
        int deltaX,
        int deltaY,
        int? sourceWidth,
        int? sourceHeight,
        int? targetWidth,
        int? targetHeight)
    {
        if (sourceWidth is not { } srcWidth
            || sourceHeight is not { } srcHeight
            || targetWidth is not { } dstWidth
            || targetHeight is not { } dstHeight)
        {
            return (deltaX, deltaY);
        }

        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            return (deltaX, deltaY);
        }

        var scaledX = (int)Math.Round(deltaX * (dstWidth / (double)srcWidth));
        var scaledY = (int)Math.Round(deltaY * (dstHeight / (double)srcHeight));
        return (scaledX, scaledY);
    }

    private static (int X, int Y) ApplyRelativeDelta(
        ref int virtualX,
        ref int virtualY,
        ref bool initialized,
        int deltaX,
        int deltaY,
        int width,
        int height)
    {
        if (!initialized)
        {
            if (width > 0 && height > 0)
            {
                virtualX = width / 2;
                virtualY = height / 2;
            }
            else
            {
                virtualX = 0;
                virtualY = 0;
            }

            initialized = true;
        }

        virtualX += deltaX;
        virtualY += deltaY;

        if (width > 0 && height > 0)
        {
            virtualX = Math.Clamp(virtualX, 0, width - 1);
            virtualY = Math.Clamp(virtualY, 0, height - 1);
        }

        return (virtualX, virtualY);
    }

    private void QueueRelativeMouseMoveForReceiver(int deltaX, int deltaY, Guid receiverId)
    {
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        lock (_mouseSendLock)
        {
            _pendingMouseDeltaX += deltaX;
            _pendingMouseDeltaY += deltaY;
            _pendingMouseTargetId = receiverId;
        }

        EnsureMouseSendLoop();
    }

    private void EnsureMouseSendLoop()
    {
        lock (_mouseSendLock)
        {
            if (_mouseSendTask != null && !_mouseSendTask.IsCompleted)
            {
                return;
            }

            _mouseSendCts?.Dispose();
            _mouseSendCts = new CancellationTokenSource();
            _mouseSendTask = Task.Run(() => MouseSendLoopAsync(_mouseSendCts.Token));
        }
    }

    private async Task MouseSendLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(MouseSendIntervalMs));
        var idleSince = DateTime.UtcNow;

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            int deltaX;
            int deltaY;
            Guid? targetId;

            lock (_mouseSendLock)
            {
                deltaX = _pendingMouseDeltaX;
                deltaY = _pendingMouseDeltaY;
                _pendingMouseDeltaX = 0;
                _pendingMouseDeltaY = 0;
                targetId = _pendingMouseTargetId;
            }

            if (deltaX == 0 && deltaY == 0)
            {
                if (DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(MouseSendIdleTimeoutMs))
                {
                    break;
                }

                continue;
            }

            idleSince = DateTime.UtcNow;

            if (targetId is null)
            {
                continue;
            }

            try
            {
                var inputEvent = new InputEvent
                {
                    EventType = InputEventType.MouseMove,
                    MouseX = deltaX,
                    MouseY = deltaY,
                    IsRelative = true
                };

                await _networkService.SendAsync(inputEvent, targetId.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send throttled mouse move.");
            }
        }

        lock (_mouseSendLock)
        {
            _mouseSendTask = null;
            _mouseSendCts?.Dispose();
            _mouseSendCts = null;
        }
    }

    private void StopMouseSendLoop()
    {
        lock (_mouseSendLock)
        {
            _pendingMouseDeltaX = 0;
            _pendingMouseDeltaY = 0;
            _pendingMouseTargetId = null;
            _mouseSendCts?.Cancel();
        }
    }

    private void ResetPendingMouse()
    {
        lock (_mouseSendLock)
        {
            _pendingMouseDeltaX = 0;
            _pendingMouseDeltaY = 0;
            _pendingMouseTargetId = null;
        }
    }

}


