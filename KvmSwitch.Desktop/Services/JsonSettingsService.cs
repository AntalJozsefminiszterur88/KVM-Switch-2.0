using System;
using System.IO;
using System.Text.Json;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using Microsoft.Extensions.Logging;

namespace KvmSwitch.Desktop.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<JsonSettingsService> _logger;
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsPath = Path.Combine(appData, "KvmSwitch", "settings.json");
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load settings.");
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save settings.");
            }
        }
    }
}
