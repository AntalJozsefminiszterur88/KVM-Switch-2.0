using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KvmSwitch.Core.Interfaces;

public interface IFileTransferService
{
    Task StartServerAsync();
    Task StartClientAsync();
    Task StartClientAsync(string? hostAddress);
    Task SendFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
    void Stop();

    event EventHandler<IReadOnlyList<string>>? FilesReceived;

    bool IsConnected { get; }
    bool IsServer { get; }
}
