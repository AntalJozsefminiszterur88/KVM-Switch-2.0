using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using KvmSwitch.Core.Interfaces;

namespace KvmSwitch.Desktop.Services;

public sealed class ScreenService : IScreenService
{
    public (double Width, double Height) GetPrimaryScreenSize()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var screen = desktop.MainWindow?.Screens?.Primary;
            if (screen != null)
            {
                return (screen.Bounds.Width, screen.Bounds.Height);
            }
        }

        return (0, 0);
    }
}
