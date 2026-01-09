using System;
using KvmSwitch.Core.Models;

namespace KvmSwitch.Core.Interfaces
{
    public interface IInputService
    {
        void SetPointerBounds(int width, int height);
        void Start();
        void Stop();
        void SimulateInput(InputEvent inputEvent);
        event EventHandler<InputEvent> InputReceived;
    }
}
