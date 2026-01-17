using System.Collections.Generic;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KvmSwitch.Core.Models;

namespace KvmSwitch.Desktop.ViewModels;

public sealed partial class ButtonMappingItemViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _targetOptions;
    private readonly Action<ButtonMappingItemViewModel> _startListening;
    private readonly Action<ButtonMappingItemViewModel> _remove;

    [ObservableProperty]
    private string buttonId = string.Empty;

    [ObservableProperty]
    private string action = string.Empty;

    [ObservableProperty]
    private int targetIndex;

    [ObservableProperty]
    private bool isListening;

    public ButtonMappingItemViewModel(
        ButtonMapping mapping,
        IReadOnlyList<string> targetOptions,
        Action<ButtonMappingItemViewModel> startListening,
        Action<ButtonMappingItemViewModel> remove)
    {
        _targetOptions = targetOptions;
        _startListening = startListening ?? throw new ArgumentNullException(nameof(startListening));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        ButtonId = mapping.ButtonId ?? string.Empty;
        Action = mapping.Action ?? string.Empty;
        TargetIndex = mapping.Target switch
        {
            ButtonMappingTarget.Remote => 2,
            ButtonMappingTarget.InputProvider => 1,
            _ => 0
        };
        StartListeningCommand = new RelayCommand(() => _startListening(this));
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    public IReadOnlyList<string> TargetOptions => _targetOptions;

    public ButtonMappingTarget Target => TargetIndex switch
    {
        2 => ButtonMappingTarget.Remote,
        1 => ButtonMappingTarget.InputProvider,
        _ => ButtonMappingTarget.Local
    };

    public bool HasButton => !string.IsNullOrWhiteSpace(ButtonId);

    public string ButtonIdDisplay => HasButton ? ButtonId : "Nincs beállítva";

    public string IndicatorColor => IsListening
        ? "#3F51B5"
        : HasButton
            ? "#16A34A"
            : "#9CA3AF";

    public string ListeningText => IsListening ? "Figyelés..." : string.Empty;

    public IRelayCommand StartListeningCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    partial void OnButtonIdChanged(string value)
    {
        OnPropertyChanged(nameof(HasButton));
        OnPropertyChanged(nameof(ButtonIdDisplay));
        OnPropertyChanged(nameof(IndicatorColor));
    }

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(IndicatorColor));
        OnPropertyChanged(nameof(ListeningText));
    }
}
