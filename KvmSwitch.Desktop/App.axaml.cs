using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
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
            mainWindow.DataContext = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = mainWindow;
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

    private void ShutdownApplication()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
        services.AddSingleton<ISerialService, PicoSerialService>();
        services.AddSingleton<IMonitorControlService, WindowsMonitorControlService>();
        services.AddSingleton<IScreenService, ScreenService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
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
