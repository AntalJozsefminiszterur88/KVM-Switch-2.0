using KvmSwitch.Core.Interfaces;

namespace KvmSwitch.Desktop.Services;

public sealed class NoOpRegistryService : IRegistryService
{
    public bool IsAutostartEnabled(string appName)
    {
        return false;
    }

    public void SetAutostartEnabled(string appName, string? executablePath, bool enabled)
    {
    }
}
