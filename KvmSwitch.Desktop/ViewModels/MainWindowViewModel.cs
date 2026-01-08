using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using Microsoft.Extensions.Logging;

namespace KvmSwitch.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public enum DeviceRole
    {
        Controller,
        InputProvider,
        Receiver
    }

    private readonly INetworkService _networkService;
    private readonly IInputService _inputService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _updatingRoleSelection;

    [ObservableProperty]
    private DeviceRole selectedRole = DeviceRole.InputProvider;

    [ObservableProperty]
    private bool isControllerSelected;

    [ObservableProperty]
    private bool isInputProviderSelected;

    [ObservableProperty]
    private bool isReceiverSelected;

    [ObservableProperty]
    private string targetIp = "192.168.0.19";

    [ObservableProperty]
    private int port = 65432;

    [ObservableProperty]
    private string hostMonitorCode = "17";

    [ObservableProperty]
    private string clientMonitorCode = "18";

    [ObservableProperty]
    private bool autoStartEnabled;

    [ObservableProperty]
    private bool isServiceRunning;

    [ObservableProperty]
    private string statusMessage = "Állapot: Inaktív";

    [ObservableProperty]
    private string buttonText = "KVM Szolgáltatás Indítása";

    [ObservableProperty]
    private string logOutput = string.Empty;

    public MainWindowViewModel(
        INetworkService networkService,
        IInputService inputService,
        ILogger<MainWindowViewModel> logger)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _inputService = inputService ?? throw new ArgumentNullException(nameof(inputService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _networkService.MessageReceived += OnMessageReceived;
        _inputService.InputReceived += OnInputReceived;

        SyncRoleSelection();
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

    partial void OnIsServiceRunningChanged(bool value)
    {
        ButtonText = value
            ? "KVM Szolgáltatás Leállítása"
            : "KVM Szolgáltatás Indítása";

        StatusMessage = value
            ? "Állapot: Aktív"
            : "Állapot: Inaktív";
    }

    [RelayCommand]
    private async Task ToggleServiceAsync()
    {
        if (IsServiceRunning)
        {
            StopServices();
            return;
        }

        SetIsServiceRunning(true);
        AppendLog($"Service starting as {SelectedRole}...");
        _logger.LogInformation("Starting KVM service as {Role}.", SelectedRole);

        try
        {
            if (SelectedRole == DeviceRole.Controller)
            {
                await _networkService.StartServerAsync(Port).ConfigureAwait(false);
            }
            else
            {
                await _networkService.ConnectToClientAsync(TargetIp, Port).ConfigureAwait(false);
            }

            if (SelectedRole == DeviceRole.InputProvider)
            {
                _inputService.Start();
            }

            AppendLog("Service started.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start KVM service.");
            AppendLog("Error: Failed to start service.");
            SetIsServiceRunning(false);
        }
    }

    [RelayCommand]
    private void OpenFileTransfer()
    {
        AppendLog("File transfer window not implemented yet.");
    }

    [RelayCommand]
    private void OpenClipboard()
    {
        AppendLog("Clipboard window not implemented yet.");
    }

    private void StopServices()
    {
        try
        {
            _networkService.Stop();
            _inputService.Stop();
            SetIsServiceRunning(false);
            AppendLog("Service stopped.");
            _logger.LogInformation("KVM service stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop KVM service.");
            AppendLog("Error: Failed to stop service.");
        }
    }

    private void OnMessageReceived(object? sender, object message)
    {
        if (message is not InputEvent inputEvent)
        {
            AppendLog("Msg received!");
            _logger.LogInformation("Message received: {MessageType}.", message?.GetType().Name ?? "null");
            return;
        }

        if (SelectedRole == DeviceRole.Receiver)
        {
            try
            {
                _inputService.SimulateInput(inputEvent);
                AppendLog($"Input simulated: {inputEvent.EventType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to simulate input event.");
                AppendLog("Error: Failed to simulate input event.");
            }
        }
        else if (SelectedRole == DeviceRole.Controller)
        {
            AppendLog($"Input reached controller: {inputEvent.EventType}");
            _logger.LogInformation("Input event reached controller: {EventType}.", inputEvent.EventType);
        }
    }

    private void OnInputReceived(object? sender, InputEvent inputEvent)
    {
        AppendLog($"Input: {inputEvent.EventType}");
        _logger.LogDebug("Input event received: {EventType}.", inputEvent.EventType);

        if (SelectedRole != DeviceRole.InputProvider || !_networkService.IsConnected)
        {
            return;
        }

        _ = SendInputAsync(inputEvent);
    }

    private async Task SendInputAsync(InputEvent inputEvent)
    {
        try
        {
            await _networkService.SendAsync(inputEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send input event.");
            AppendLog("Error: Failed to send input event.");
        }
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
}
