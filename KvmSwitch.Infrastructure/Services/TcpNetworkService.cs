using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using MessagePack;
using MessagePack.Resolvers;
using Serilog;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class TcpNetworkService : INetworkService
    {
        private static readonly MessagePackSerializerOptions SerializerOptions =
            MessagePackSerializerOptions.Standard.WithResolver(TypelessContractlessStandardResolver.Instance);

        private readonly object _sync = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _connectionCts;
        private Task? _receiveTask;
        private volatile bool _isConnected;

        public event EventHandler<object>? MessageReceived;

        public bool IsConnected => _isConnected;

        public async Task StartServerAsync(int port)
        {
            TcpListener? listener;
            CancellationTokenSource? cts;

            lock (_sync)
            {
                if (_listener != null || _client != null)
                {
                    Log.Warning("TCP server already running.");
                    return;
                }

                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _connectionCts = new CancellationTokenSource();
                listener = _listener;
                cts = _connectionCts;
            }

            Log.Information("TCP server listening on port {Port}.", port);

            try
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                InitializeConnection(client, cts.Token, "server accept");
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP server accept canceled.");
            }
            catch (ObjectDisposedException)
            {
                Log.Information("TCP server listener disposed.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TCP server failed while accepting a client.");
                throw;
            }
        }

        public async Task ConnectToClientAsync(string ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address is required.", nameof(ip));
            }

            CancellationTokenSource? cts;

            lock (_sync)
            {
                if (_listener != null || _client != null)
                {
                    Log.Warning("TCP client already connected or server running.");
                    return;
                }

                _connectionCts = new CancellationTokenSource();
                cts = _connectionCts;
            }

            var client = new TcpClient { NoDelay = true };

            try
            {
                await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
                InitializeConnection(client, cts.Token, "client connect");
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP client connection canceled.");
                client.Close();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TCP client failed to connect to {Ip}:{Port}.", ip, port);
                client.Close();
                throw;
            }
        }

        public async Task SendAsync<T>(T message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            NetworkStream stream;

            lock (_sync)
            {
                if (!_isConnected || _stream == null)
                {
                    throw new InvalidOperationException("Not connected.");
                }

                stream = _stream;
            }

            byte[] payload;

            try
            {
                payload = MessagePackSerializer.Serialize(message, SerializerOptions);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to serialize message of type {MessageType}.", message.GetType().FullName);
                throw;
            }

            var lengthBytes = BitConverter.GetBytes(payload.Length);

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length).ConfigureAwait(false);
                await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send message.");
                HandleDisconnect("Connection lost while sending.", ex);
                throw;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Stop()
        {
            TcpListener? listener;
            TcpClient? client;
            NetworkStream? stream;
            CancellationTokenSource? cts;

            lock (_sync)
            {
                listener = _listener;
                _listener = null;
                client = _client;
                _client = null;
                stream = _stream;
                _stream = null;
                cts = _connectionCts;
                _connectionCts = null;
                _receiveTask = null;
                _isConnected = false;
            }

            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while cancelling TCP network service.");
            }

            try
            {
                stream?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while disposing TCP network stream.");
            }

            try
            {
                client?.Close();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while closing TCP client.");
            }

            try
            {
                listener?.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping TCP listener.");
            }

            Log.Information("TCP network service stopped.");
        }

        private void InitializeConnection(TcpClient client, CancellationToken token, string source)
        {
            NetworkStream stream;

            lock (_sync)
            {
                _client = client;
                _stream = client.GetStream();
                _isConnected = true;
                stream = _stream;
            }

            Log.Information("TCP connection established via {Source} ({RemoteEndPoint}).", source, client.Client.RemoteEndPoint);
            _receiveTask = ReceiveLoopAsync(stream, token);
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                var lengthBuffer = new byte[sizeof(int)];

                while (!token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, lengthBuffer, token).ConfigureAwait(false))
                    {
                        HandleDisconnect("Remote connection closed.");
                        return;
                    }

                    int length = BitConverter.ToInt32(lengthBuffer, 0);
                    if (length <= 0)
                    {
                        HandleDisconnect("Invalid message length received.");
                        return;
                    }

                    var payload = new byte[length];
                    if (!await ReadExactAsync(stream, payload, token).ConfigureAwait(false))
                    {
                        HandleDisconnect("Remote connection closed while reading payload.");
                        return;
                    }

                    object? message;
                    try
                    {
                        message = MessagePackSerializer.Deserialize<object>(payload, SerializerOptions);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to deserialize incoming message.");
                        continue;
                    }

                    if (message != null)
                    {
                        MessageReceived?.Invoke(this, message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP receive loop canceled.");
            }
            catch (IOException ex)
            {
                HandleDisconnect("TCP receive loop aborted due to IO error.", ex);
            }
            catch (SocketException ex)
            {
                HandleDisconnect("TCP receive loop aborted due to socket error.", ex);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in TCP receive loop.");
                HandleDisconnect("TCP receive loop terminated unexpectedly.", ex);
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }

        private void HandleDisconnect(string message, Exception? ex = null)
        {
            if (ex == null)
            {
                Log.Warning(message);
            }
            else
            {
                Log.Warning(ex, message);
            }

            TcpClient? client;
            NetworkStream? stream;

            lock (_sync)
            {
                client = _client;
                _client = null;
                stream = _stream;
                _stream = null;
                _isConnected = false;
            }

            try
            {
                stream?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warning(disposeEx, "Error while disposing TCP network stream.");
            }

            try
            {
                client?.Close();
            }
            catch (Exception closeEx)
            {
                Log.Warning(closeEx, "Error while closing TCP client.");
            }
        }
    }
}
