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
            window.Hide();
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
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
            .WriteTo.File("logs/kvm-switch-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(Log.Logger);
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));

        services.AddSingleton<IInputService, SharpHookInputService>();
        services.AddSingleton<INetworkService, TcpNetworkService>();
        services.AddSingleton<IDataNetworkService, DataNetworkService>();
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
