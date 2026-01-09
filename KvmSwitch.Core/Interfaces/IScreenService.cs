namespace KvmSwitch.Core.Interfaces;

public interface IScreenService
{
    (double Width, double Height) GetPrimaryScreenSize();
}
