using System;
using System.Threading.Tasks;

namespace KvmSwitch.Core.Interfaces;

public interface IDataNetworkService
{
    Task StartServerAsync();
    Task StartClientAsync();
    Task StartClientAsync(string? hostAddress);
    Task SendAsync<T>(T message, Guid? targetClientId = null);
    void Stop();

    event EventHandler<(object Message, Guid ClientId)> MessageReceived;
    event EventHandler<Guid> ClientConnected;
    event EventHandler<Guid> ClientDisconnected;

    bool IsConnected { get; }
    bool IsServer { get; }
}
