using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;

namespace KvmSwitch.Desktop.ViewModels;

public sealed partial class ButtonMappingViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<string> TargetOptions =
        new[] { "Helyi (EliteDesk)", "Asztali gép (Input Provider)", "Távoli (Laptop/Client)" };

    private readonly ISerialService _serialService;
    private readonly ISettingsService _settingsService;
    private readonly object _sync = new();
    private readonly List<ButtonMapping> _mappingStore = new();
    private ButtonMappingItemViewModel? _listeningItem;

    public ObservableCollection<ButtonMappingItemViewModel> Mappings { get; } = new();

    public event Action? RequestClose;

    [ObservableProperty]
    private bool isListening;

    public ButtonMappingViewModel(ISerialService serialService, ISettingsService settingsService)
    {
        _serialService = serialService ?? throw new ArgumentNullException(nameof(serialService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        LoadMappings();
        _serialService.RawButtonReceived += OnRawButtonReceived;
    }

    public bool TryGetMapping(string buttonId, out ButtonMapping mapping)
    {
        mapping = new ButtonMapping();
        if (string.IsNullOrWhiteSpace(buttonId))
        {
            return false;
        }

        var trimmed = buttonId.Trim();
        lock (_sync)
        {
            var match = _mappingStore.FirstOrDefault(entry =>
                string.Equals(entry.ButtonId, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return false;
            }

            mapping = new ButtonMapping
            {
                ButtonId = match.ButtonId,
                Target = match.Target,
                Action = match.Action
            };
        }

        return true;
    }

    [RelayCommand]
    private void AddMapping()
    {
        var item = new ButtonMappingItemViewModel(new ButtonMapping(), TargetOptions, StartListening, RemoveMapping);
        AttachItem(item);
        Mappings.Add(item);
        StartListening(item);
        SaveMappings();
    }

    [RelayCommand]
    private void Save()
    {
        SaveMappings();
    }

    private void RemoveMapping(ButtonMappingItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        if (ReferenceEquals(_listeningItem, item))
        {
            StopListening();
        }

        DetachItem(item);
        Mappings.Remove(item);
        SaveMappings();
    }

    private void StartListening(ButtonMappingItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        if (_listeningItem != null && !ReferenceEquals(_listeningItem, item))
        {
            _listeningItem.IsListening = false;
        }

        _listeningItem = item;
        _listeningItem.IsListening = true;
        IsListening = true;
    }

    [RelayCommand]
    private void Close()
    {
        StopListening();
        RequestClose?.Invoke();
    }

    private void StopListening()
    {
        if (_listeningItem != null)
        {
            _listeningItem.IsListening = false;
            _listeningItem = null;
        }

        IsListening = false;
    }

    private void OnRawButtonReceived(object? sender, string buttonId)
    {
        if (_listeningItem == null)
        {
            return;
        }

        var trimmed = buttonId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyButtonId(trimmed);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyButtonId(trimmed));
    }

    private void ApplyButtonId(string buttonId)
    {
        if (_listeningItem == null)
        {
            return;
        }

        _listeningItem.ButtonId = buttonId;
        _listeningItem.IsListening = false;
        _listeningItem = null;
        IsListening = false;
        SaveMappings();
    }

    private void LoadMappings()
    {
        var settings = _settingsService.Load();
        var mappings = settings.ButtonMappings ?? new List<ButtonMapping>();

        lock (_sync)
        {
            _mappingStore.Clear();
            _mappingStore.AddRange(mappings.Select(CloneMapping));
        }

        Mappings.Clear();
        foreach (var mapping in mappings)
        {
        var item = new ButtonMappingItemViewModel(mapping, TargetOptions, StartListening, RemoveMapping);
        AttachItem(item);
        Mappings.Add(item);
    }
    }

    private void SaveMappings()
    {
        var models = Mappings
            .Select(item => new ButtonMapping
            {
                ButtonId = (item.ButtonId ?? string.Empty).Trim(),
                Target = item.Target,
                Action = item.Action ?? string.Empty
            })
            .ToList();

        lock (_sync)
        {
            _mappingStore.Clear();
            _mappingStore.AddRange(models.Select(CloneMapping));
        }

        var settings = _settingsService.Load();
        settings.ButtonMappings = models;
        _settingsService.Save(settings);
    }

    private void AttachItem(ButtonMappingItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void DetachItem(ButtonMappingItemViewModel item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ButtonMappingItemViewModel.ButtonId)
            || e.PropertyName == nameof(ButtonMappingItemViewModel.Action)
            || e.PropertyName == nameof(ButtonMappingItemViewModel.TargetIndex))
        {
            SaveMappings();
        }
    }

    private static ButtonMapping CloneMapping(ButtonMapping mapping)
    {
        return new ButtonMapping
        {
            ButtonId = mapping.ButtonId,
            Target = mapping.Target,
            Action = mapping.Action
        };
    }
}
