using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using KvmSwitch.Core.Interfaces;
using Microsoft.Win32;
using Serilog;

namespace KvmSwitch.Desktop.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryService : IRegistryService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartArgument = "--autostart";

    public bool IsAutostartEnabled(string appName)
    {
        if (!IsWindows() || string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        if (TaskExists(appName))
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read autostart value for {AppName}.", appName);
            return false;
        }
    }

    public void SetAutostartEnabled(string appName, string? executablePath, bool enabled)
    {
        if (!IsWindows() || string.IsNullOrWhiteSpace(appName))
        {
            return;
        }

        try
        {
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    Log.Warning("Executable path not available for autostart.");
                    return;
                }

                var taskCreated = TryCreateTask(appName, executablePath);
                if (taskCreated)
                {
                    RemoveRunKey(appName);
                }
                else
                {
                    EnsureRunKey(appName, executablePath);
                }
            }
            else
            {
                DeleteTask(appName);
                RemoveRunKey(appName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set autostart value for {AppName}.", appName);
        }
    }

    private static bool IsWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    private static bool TaskExists(string taskName)
    {
        var result = RunSchtasks($"/Query /TN \"{taskName}\"");
        return result.ExitCode == 0;
    }

    private static bool TryCreateTask(string taskName, string executablePath)
    {
        var quotedPath = Quote(executablePath);
        var taskCommand = $"{quotedPath} {AutoStartArgument}";
        var args = $"/Create /TN \"{taskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"{taskCommand}\"";
        var result = RunSchtasks(args);

        if (result.ExitCode == 0)
        {
            return true;
        }

        Log.Warning("Failed to create scheduled task {TaskName}. ExitCode={ExitCode} Error={Error}",
            taskName, result.ExitCode, result.StandardError);
        return false;
    }

    private static void DeleteTask(string taskName)
    {
        var result = RunSchtasks($"/Delete /TN \"{taskName}\" /F");
        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            Log.Warning("Failed to delete scheduled task {TaskName}. ExitCode={ExitCode} Error={Error}",
                taskName, result.ExitCode, result.StandardError);
        }
    }

    private static void EnsureRunKey(string appName, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null)
        {
            Log.Warning("Run registry key not available.");
            return;
        }

        var command = $"{Quote(executablePath)} {AutoStartArgument}";
        key.SetValue(appName, command);
    }

    private static void RemoveRunKey(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(appName, throwOnMissingValue: false);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunSchtasks(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = arguments,
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
                return (-1, string.Empty, "Failed to start schtasks.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to run schtasks.");
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value}\"";
    }
}
