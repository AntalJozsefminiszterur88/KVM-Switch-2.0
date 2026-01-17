using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Desktop.Services;
using KvmSwitch.Desktop.ViewModels;
using KvmSwitch.Desktop.Views;
using KvmSwitch.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KvmSwitch.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;
    internal static bool IsExiting { get; private set; }
    private static int _showSequence;
    private static double? _baseOpacity;
    private static WindowStartupLocation? _baseStartupLocation;
    private static EventHandler? _layoutUpdatedHandler;
    private static int _pendingShowId;
    private static readonly TimeSpan ShowDebounce = TimeSpan.FromMilliseconds(100);
    private static Size? _lastWindowSize;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Services = ConfigureServices();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var mainWindow = Services.GetRequiredService<MainWindow>();
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = viewModel;
            desktop.MainWindow = mainWindow;

            var startHidden = viewModel.StartInTray || HasAutoStartArg();
            if (startHidden)
            {
                WarmUpMainWindow(mainWindow);
                Dispatcher.UIThread.Post(mainWindow.Hide);
            }

            EnsureTrayIcon();
            DispatcherTimer.RunOnce(EnsureTrayIcon, TimeSpan.FromSeconds(3));

            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ToggleMainWindow);
    }

    private void TrayIcon_ToggleWindow(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ToggleMainWindow);
    }

    private void TrayIcon_Exit(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ShutdownApplication);
    }

    private void EnsureTrayIcon()
    {
        var trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (trayIcon == null)
        {
            return;
        }

        try
        {
            var uri = new Uri("avares://KvmSwitch.Desktop/Assets/logo.ico");
            using var stream = AssetLoader.Open(uri);
            trayIcon.Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set tray icon.");
        }
    }

    private void ToggleMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var window = desktop.MainWindow;
        if (window is null)
        {
            return;
        }

        if (window.IsVisible && window.WindowState != WindowState.Minimized)
        {
            CancelPendingShow(window);
            window.Hide();
            return;
        }

        ScheduleShowWindow(window);
    }

    private void WarmUpMainWindow(Window mainWindow)
    {
        var originalOpacity = mainWindow.Opacity;
        var originalShowInTaskbar = mainWindow.ShowInTaskbar;
        var originalStartupLocation = mainWindow.WindowStartupLocation;
        var originalPosition = mainWindow.Position;
        var originalWindowState = mainWindow.WindowState;

        mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        mainWindow.Position = new PixelPoint(-32000, -32000);
        mainWindow.WindowState = WindowState.Minimized;
        mainWindow.Opacity = 0;
        mainWindow.ShowInTaskbar = false;
        mainWindow.Show();

        Dispatcher.UIThread.Post(() =>
        {
            mainWindow.Hide();
            mainWindow.Opacity = originalOpacity;
            mainWindow.ShowInTaskbar = originalShowInTaskbar;
            mainWindow.WindowState = originalWindowState;
            mainWindow.WindowStartupLocation = originalStartupLocation;
            if (originalStartupLocation == WindowStartupLocation.Manual)
            {
                mainWindow.Position = originalPosition;
            }
            UpdateLastWindowSize(mainWindow);
        }, DispatcherPriority.Background);
    }

    internal static bool TryCenterWindow(Window window, Size? sizeOverride = null)
    {
        if (window.Screens == null)
        {
            return false;
        }

        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen == null)
        {
            return false;
        }

        var bounds = window.Bounds;
        var width = sizeOverride?.Width ?? (bounds.Width > 0 ? bounds.Width : window.Width);
        var height = sizeOverride?.Height ?? (bounds.Height > 0 ? bounds.Height : window.Height);
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return false;
        }

        var x = screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - width) / 2.0);
        var y = screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - height) / 2.0);
        window.Position = new PixelPoint(x, y);
        return true;
    }

    internal static void ShowWindowCentered(Window window)
    {
        if (_baseOpacity == null || _baseOpacity <= 0)
        {
            _baseOpacity = window.Opacity > 0 ? window.Opacity : 1;
        }

        if (_baseStartupLocation == null)
        {
            _baseStartupLocation = window.WindowStartupLocation;
        }

        var sequence = ++_showSequence;
        var baseOpacity = _baseOpacity.Value;
        var baseStartupLocation = _baseStartupLocation.Value;

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        if (_lastWindowSize.HasValue && TryCenterWindow(window, _lastWindowSize))
        {
            window.Opacity = baseOpacity;
            window.Show();
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.Post(() =>
            {
                TryCenterWindow(window, _lastWindowSize);
                UpdateLastWindowSize(window);
                window.Activate();
            }, DispatcherPriority.Background);
            return;
        }

        window.Opacity = 0;
        window.Position = new PixelPoint(-32000, -32000);

        window.Show();
        window.WindowState = WindowState.Normal;

        if (_layoutUpdatedHandler != null)
        {
            window.LayoutUpdated -= _layoutUpdatedHandler;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (sequence != _showSequence || !window.IsVisible)
            {
                window.LayoutUpdated -= handler;
                return;
            }

            if (!TryCenterWindow(window))
            {
                return;
            }

            window.LayoutUpdated -= handler;
            window.Opacity = baseOpacity;
            UpdateLastWindowSize(window);
            window.WindowStartupLocation = baseStartupLocation;
            Dispatcher.UIThread.Post(window.Activate, DispatcherPriority.Background);
        };

        _layoutUpdatedHandler = handler;
        window.LayoutUpdated += handler;
    }

    private static void UpdateLastWindowSize(Window window)
    {
        var bounds = window.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _lastWindowSize = bounds.Size;
        }
    }

    internal static void CancelPendingShow(Window window)
    {
        _showSequence++;
        _pendingShowId++;
        if (_layoutUpdatedHandler != null)
        {
            window.LayoutUpdated -= _layoutUpdatedHandler;
        }

        if (_baseOpacity.HasValue)
        {
            window.Opacity = _baseOpacity.Value;
        }

        if (_baseStartupLocation.HasValue)
        {
            window.WindowStartupLocation = _baseStartupLocation.Value;
        }
    }

    private static void ScheduleShowWindow(Window window)
    {
        var requestId = ++_pendingShowId;
        DispatcherTimer.RunOnce(() =>
        {
            if (requestId != _pendingShowId)
            {
                return;
            }

            if (window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                return;
            }

            ShowWindowCentered(window);
        }, ShowDebounce);
    }

    private async void ShutdownApplication()
    {
        IsExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var viewModel = Services.GetRequiredService<MainWindowViewModel>();
                await viewModel.ShutdownAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to stop services during shutdown.");
            }
            desktop.Shutdown();
            return;
        }

        Environment.Exit(0);
    }

    private static ServiceProvider ConfigureServices()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/kvm-switch-.log", rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(Log.Logger);
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));

        services.AddSingleton<IInputService, SharpHookInputService>();
        services.AddSingleton<INetworkService, TcpNetworkService>();
        services.AddSingleton<IDataNetworkService, DataNetworkService>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<ISerialService, PicoSerialService>();
        services.AddSingleton<IMonitorControlService, WindowsMonitorControlService>();
        services.AddSingleton<IScreenService, ScreenService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IRegistryService, WindowsRegistryService>();
        }
        else
        {
            services.AddSingleton<IRegistryService, NoOpRegistryService>();
        }
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();

        services.AddSingleton<ButtonMappingViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<FileTransferViewModel>();
        services.AddTransient<FileTransferWindow>();

        return services.BuildServiceProvider();
    }

    private static bool HasAutoStartArg()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Any(arg =>
            string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--start-hidden", StringComparison.OrdinalIgnoreCase));
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

}
