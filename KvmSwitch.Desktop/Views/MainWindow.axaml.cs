using System;
using Avalonia.Controls;

namespace KvmSwitch.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
            e.Cancel = true; // Prevent actual closing
            Hide();          // Just hide the window
        }

        base.OnClosing(e);
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
