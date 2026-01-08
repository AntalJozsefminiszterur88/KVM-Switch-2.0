using System.Threading.Tasks;

namespace KvmSwitch.Core.Interfaces
{
    public interface IMonitorControlService
    {
        Task SwitchInputAsync(int inputSourceId);
    }
}
