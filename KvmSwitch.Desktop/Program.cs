using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Serilog;

namespace KvmSwitch.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            WriteStartupLog("Starting application.");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            WriteStartupLog("Application lifetime ended.");
        }
        catch (Exception ex)
        {
            WriteStartupLog($"Fatal exception: {ex}");
            Log.Fatal(ex, "Fatal exception in application entry point.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteStartupLog($"Unhandled exception (terminating={e.IsTerminating}): {ex}");
            Log.Fatal(ex, "Unhandled exception (terminating={IsTerminating}).", e.IsTerminating);
        }
        else
        {
            WriteStartupLog($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
            Log.Fatal("Unhandled exception (terminating={IsTerminating}).", e.IsTerminating);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteStartupLog($"Unobserved task exception: {e.Exception}");
        Log.Fatal(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private static void WriteStartupLog(string message)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "startup.log");
            File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures during startup.
        }
    }
}
