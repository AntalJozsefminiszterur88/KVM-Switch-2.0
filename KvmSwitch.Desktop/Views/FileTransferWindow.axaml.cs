using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KvmSwitch.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KvmSwitch.Desktop.Views;

public partial class FileTransferWindow : Window
{
    private const double DefaultWidth = 1000;
    private const double DefaultHeight = 720;
    private const double DefaultMinWidth = 900;
    private const double DefaultMinHeight = 640;
    private const double MinWidthFloor = 720;
    private const double MinHeightFloor = 520;
    private const double ScreenPaddingRatio = 0.9;

    public FileTransferWindow()
        : this(App.Services.GetRequiredService<FileTransferViewModel>())
    {
    }

    public FileTransferWindow(FileTransferViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += ViewModelOnRequestClose;
        Opened += OnOpened;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (App.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        if (!e.IsProgrammatic
            && (e.CloseReason == WindowCloseReason.WindowClosing
                || e.CloseReason == WindowCloseReason.Undefined))
        {
            e.Cancel = true;
            Hide();
            ShowMainWindow();
            return;
        }

        if (DataContext is FileTransferViewModel viewModel)
        {
            viewModel.RequestClose -= ViewModelOnRequestClose;
            viewModel.Shutdown();
        }

        base.OnClosing(e);
    }

    private void ViewModelOnRequestClose()
    {
        Hide();
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow is null)
            {
                return;
            }

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    private async void BrowseDestination_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Célmappa kiválasztása"
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        viewModel.ReceiverDestination = folder.Path.LocalPath;
    }

    private async void AddFiles_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Fájlok kiválasztása"
        });

        if (files.Count == 0)
        {
            return;
        }

        viewModel.AddFiles(files.Select(file => file.Path.LocalPath));
    }

    private async void AddFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Mappa kiválasztása"
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        viewModel.AddDirectory(folder.Path.LocalPath);
    }

    private void RemoveSelected_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel viewModel)
        {
            return;
        }

        var selected = FilesListBox.SelectedItems?.OfType<string>().ToList();
        if (selected is null || selected.Count == 0)
        {
            return;
        }

        viewModel.RemoveFiles(selected);
    }

    private void Clear_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileTransferViewModel viewModel)
        {
            viewModel.ClearFiles();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        AdjustWindowSizeForScreen();
    }

    private void AdjustWindowSizeForScreen()
    {
        var screen = Screens?.Primary;
        if (screen == null)
        {
            return;
        }

        var maxWidth = screen.WorkingArea.Width * ScreenPaddingRatio;
        var maxHeight = screen.WorkingArea.Height * ScreenPaddingRatio;
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return;
        }

        var targetWidth = Math.Min(DefaultWidth, maxWidth);
        var targetHeight = Math.Min(DefaultHeight, maxHeight);
        if (targetWidth >= DefaultWidth && targetHeight >= DefaultHeight)
        {
            return;
        }

        Width = targetWidth;
        Height = targetHeight;
        MinWidth = Math.Max(MinWidthFloor, Math.Min(DefaultMinWidth, targetWidth));
        MinHeight = Math.Max(MinHeightFloor, Math.Min(DefaultMinHeight, targetHeight));

        var centeredX = screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - targetWidth) / 2.0);
        var centeredY = screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - targetHeight) / 2.0);
        Position = new PixelPoint(centeredX, centeredY);
    }
}

