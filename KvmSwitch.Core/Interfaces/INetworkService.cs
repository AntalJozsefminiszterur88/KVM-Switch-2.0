using System;
using System.Threading.Tasks;

namespace KvmSwitch.Core.Interfaces
{
    public interface INetworkService
    {
        Task StartServerAsync(int port);
        Task ConnectToClientAsync(string ip, int port);
        Task SendAsync<T>(T message);
        void Stop();

        event EventHandler<object> MessageReceived;
        bool IsConnected { get; }
    }
}
