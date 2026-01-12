using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using KvmSwitch.Core.Interfaces;
using Serilog;

namespace KvmSwitch.Desktop.Services;

public sealed class ScreenService : IScreenService
{
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromSeconds(2);
    private static int _loggedNonUiThreadAccess;
    private static int _loggedNoScreen;
    private double _cachedWidth;
    private double _cachedHeight;
    private long _lastRefreshTicks;
    private int _refreshScheduled;

    public ScreenService()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshCache();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshCache);
        }
    }

    public (double Width, double Height) GetPrimaryScreenSize()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshCache();
            return ReadCache();
        }

        if (Interlocked.Exchange(ref _loggedNonUiThreadAccess, 1) == 0)
        {
            Log.Debug("GetPrimaryScreenSize using cached value on non-UI thread {ThreadId}.", Environment.CurrentManagedThreadId);
        }

        var (width, height) = ReadCache();
        if (width <= 0 || height <= 0 || IsCacheStale())
        {
            ScheduleRefresh();
        }

        return (width, height);
    }

    private (double Width, double Height) ReadCache()
    {
        return (Volatile.Read(ref _cachedWidth), Volatile.Read(ref _cachedHeight));
    }

    private bool IsCacheStale()
    {
        var lastTicks = Volatile.Read(ref _lastRefreshTicks);
        if (lastTicks == 0)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(lastTicks) > CacheMaxAge;
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(RefreshCache);
    }

    private void RefreshCache()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var screen = desktop.MainWindow?.Screens?.Primary;
                if (screen != null)
                {
                    Volatile.Write(ref _cachedWidth, screen.Bounds.Width);
                    Volatile.Write(ref _cachedHeight, screen.Bounds.Height);
                    Volatile.Write(ref _lastRefreshTicks, Stopwatch.GetTimestamp());
                    return;
                }

                if (Interlocked.Exchange(ref _loggedNoScreen, 1) == 0)
                {
                    Log.Debug("GetPrimaryScreenSize found no primary screen. MainWindow={HasWindow}.", desktop.MainWindow != null);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
        }
    }
}
