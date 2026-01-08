using System;
using KvmSwitch.Core.Models;

namespace KvmSwitch.Core.Interfaces
{
    public interface IInputService
    {
        void Start();
        void Stop();
        void SimulateInput(InputEvent inputEvent);
        event EventHandler<InputEvent> InputReceived;
    }
}
