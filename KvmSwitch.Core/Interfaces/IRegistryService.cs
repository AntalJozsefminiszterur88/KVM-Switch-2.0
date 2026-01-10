namespace KvmSwitch.Core.Interfaces;

public interface IRegistryService
{
    bool IsAutostartEnabled(string appName);
    void SetAutostartEnabled(string appName, string? executablePath, bool enabled);
}
