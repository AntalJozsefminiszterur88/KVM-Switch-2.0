using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KvmSwitch.Core.Interfaces
{
    public interface IClipboardService
    {
        Task SetTextAsync(string text);
        Task SetFilesAsync(IEnumerable<string> filePaths);

        event EventHandler<string> ClipboardTextChanged;
        event EventHandler<IEnumerable<string>> ClipboardFilesChanged;
    }
}
