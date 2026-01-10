using KvmSwitch.Core.Models;

namespace KvmSwitch.Core.Interfaces
{
    public interface ISettingsService
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }
}
