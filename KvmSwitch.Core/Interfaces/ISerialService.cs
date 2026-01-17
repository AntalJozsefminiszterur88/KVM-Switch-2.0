using System;

namespace KvmSwitch.Core.Interfaces
{
    public interface ISerialService
    {
        void Start(string portName);
        void Stop();

        event EventHandler<string> CommandReceived;
        event EventHandler<string>? RawButtonReceived;
    }
}
