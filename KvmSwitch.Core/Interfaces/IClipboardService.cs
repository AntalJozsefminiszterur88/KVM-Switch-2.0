using System;

namespace KvmSwitch.Core.Interfaces;

public interface IClipboardService
{
    void Start();
    void Stop();

    event EventHandler<string>? ClipboardTextChanged;
}
