using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using Makaretu.Dns;
using MessagePack;
using MessagePack.Resolvers;
using Serilog;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class DataNetworkService : IDataNetworkService
    {
        private sealed class ClientConnection : IDisposable
        {
            public ClientConnection(TcpClient client)
            {
                Client = client;
                Stream = client.GetStream();
            }

            public TcpClient Client { get; }
            public NetworkStream Stream { get; }
            public SemaphoreSlim WriteLock { get; } = new(1, 1);

            public void Dispose()
            {
                Stream.Dispose();
                Client.Close();
                WriteLock.Dispose();
            }
        }

        private static readonly MessagePackSerializerOptions SerializerOptions =
            MessagePackSerializerOptions.Standard.WithResolver(TypelessContractlessStandardResolver.Instance);
        private const int DataPort = 54545;
        private const string ServiceType = "_kvmswitch-data._tcp";
        private const string ServiceInstanceName = "kvmclipboard";
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
        private static readonly DomainName ServiceTypeName = new(ServiceType);

        private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
        private readonly object _sync = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private Task? _clientLoopTask;
        private ServiceDiscovery? _serviceDiscovery;
        private ServiceProfile? _serviceProfile;
        private int _connectionInProgress;
        private volatile bool _isConnected;
        private volatile bool _isServer;

        public event EventHandler<(object Message, Guid ClientId)>? MessageReceived;
        public event EventHandler<Guid>? ClientConnected;
        public event EventHandler<Guid>? ClientDisconnected;

        public bool IsConnected => _isConnected;
        public bool IsServer => _isServer;

        public Task StartServerAsync()
        {
            lock (_sync)
            {
                if (_listener != null)
                {
                    return Task.CompletedTask;
                }

                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, DataPort);
                try
                {
                    _listener.Start();
                }
                catch
                {
                    _listener = null;
                    _cts.Dispose();
                    _cts = null;
                    throw;
                }

                _isServer = true;
                _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
                StartServiceAdvertisement();
            }

            Log.Information("Data TCP server listening on port {Port}.", DataPort);
            return Task.CompletedTask;
        }

        public Task StartClientAsync()
        {
            return StartClientAsync(null);
        }

        public Task StartClientAsync(string? hostAddress)
        {
            lock (_sync)
            {
                if (_listener != null)
                {
                    Log.Warning("Data TCP service is already running as a server.");
                    return Task.CompletedTask;
                }

                if (_cts != null || !_clients.IsEmpty)
                {
                    Log.Warning("Data TCP client already running.");
                    return Task.CompletedTask;
                }

                _cts = new CancellationTokenSource();
                _isServer = false;

                if (string.IsNullOrWhiteSpace(hostAddress))
                {
                    _serviceDiscovery = new ServiceDiscovery();
                    _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
                    try
                    {
                        _serviceDiscovery.QueryServiceInstances(ServiceTypeName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Data service discovery query failed.");
                    }

                    _clientLoopTask = Task.Run(() => ClientLoopAsync(_cts.Token));
                    Log.Information("Data TCP client auto-discovery started.");
                    return Task.CompletedTask;
                }

                var targetHost = hostAddress.Trim();
                _clientLoopTask = Task.Run(() => DirectConnectLoopAsync(targetHost, _cts.Token));
                Log.Information("Data TCP client direct connect started to {Host}:{Port}.", targetHost, DataPort);
            }

            return Task.CompletedTask;
        }

        public async Task SendAsync<T>(T message, Guid? targetClientId = null)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            byte[] payload;
            try
            {
                payload = MessagePackSerializer.Serialize<object>(message, SerializerOptions);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to serialize data message of type {MessageType}.", message.GetType().FullName);
                throw;
            }

            if (targetClientId.HasValue)
            {
                if (_clients.TryGetValue(targetClientId.Value, out var connection))
                {
                    await SendToConnectionAsync(targetClientId.Value, connection, payload).ConfigureAwait(false);
                }
                else
                {
                    Log.Warning("Target client {ClientId} not found.", targetClientId.Value);
                }

                return;
            }

            foreach (var kvp in _clients)
            {
                await SendToConnectionAsync(kvp.Key, kvp.Value, payload).ConfigureAwait(false);
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            TcpListener? listener;
            ServiceDiscovery? serviceDiscovery;

            lock (_sync)
            {
                cts = _cts;
                _cts = null;
                listener = _listener;
                _listener = null;
                _acceptTask = null;
                _clientLoopTask = null;
                serviceDiscovery = _serviceDiscovery;
                _serviceDiscovery = null;
                _serviceProfile = null;
                _isServer = false;
                _connectionInProgress = 0;
            }

            if (serviceDiscovery != null)
            {
                try
                {
                    serviceDiscovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
                    serviceDiscovery.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error while stopping data service discovery.");
                }
            }

            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping data TCP service.");
            }

            try
            {
                listener?.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping data TCP listener.");
            }

            foreach (var clientId in _clients.Keys)
            {
                RemoveClient(clientId, "Data TCP service stopped.");
            }

            UpdateConnectionState();
            Log.Information("Data TCP network service stopped.");
        }

        private void StartServiceAdvertisement()
        {
            if (_serviceDiscovery != null)
            {
                return;
            }

            try
            {
                _serviceProfile = new ServiceProfile(
                    new DomainName(ServiceInstanceName),
                    ServiceTypeName,
                    (ushort)DataPort,
                    GetLocalAddresses());

                _serviceDiscovery = new ServiceDiscovery();
                _serviceDiscovery.Advertise(_serviceProfile);
                Log.Information("Data mDNS service advertised as {Name} on port {Port}.", ServiceInstanceName, DataPort);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to advertise data mDNS service.");
            }
        }

        private static IReadOnlyList<IPAddress> GetLocalAddresses()
        {
            var overrideIp = Environment.GetEnvironmentVariable("KVM_SWITCH_ADVERTISE_IP");
            if (!string.IsNullOrWhiteSpace(overrideIp) && IPAddress.TryParse(overrideIp, out var parsedIp))
            {
                return new[] { parsedIp };
            }

            var candidates = new List<(NetworkInterface Nic, IPAddress Ip, int Score)>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsEligibleInterface(nic))
                {
                    continue;
                }

                var ipProps = nic.GetIPProperties();
                var hasIpv4Gateway = ipProps.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork);

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (!IsUsableIpv4(address))
                    {
                        continue;
                    }

                    var score = ScoreInterface(nic, address, hasIpv4Gateway);
                    candidates.Add((nic, address, score));
                }
            }

            if (candidates.Count == 0)
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(address => !IPAddress.IsLoopback(address))
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Distinct()
                    .ToArray();
            }

            var ordered = candidates.OrderByDescending(candidate => candidate.Score).ToList();
            return new[] { ordered[0].Ip };
        }

        private static bool IsEligibleInterface(NetworkInterface nic)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                return false;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                || nic.NetworkInterfaceType == NetworkInterfaceType.Unknown)
            {
                return false;
            }

            return !IsDisallowedInterface(nic.Name, nic.Description);
        }

        private static bool IsDisallowedInterface(string? name, string? description)
        {
            var combined = $"{name} {description}".ToLowerInvariant();
            string[] tokens =
            {
                "virtual",
                "hyper-v",
                "wsl",
                "vmware",
                "vbox",
                "docker",
                "tunnel",
                "teredo",
                "vpn",
                "wireguard",
                "zerotier",
                "tailscale",
                "hamachi",
                "bluetooth"
            };

            return tokens.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUsableIpv4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            if (IPAddress.IsLoopback(address))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            return true;
        }

        private static int ScoreInterface(NetworkInterface nic, IPAddress address, bool hasIpv4Gateway)
        {
            var score = 0;
            if (hasIpv4Gateway)
            {
                score += 100;
            }

            if (IsPrivateIpv4(address))
            {
                score += 50;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            {
                score += 30;
            }
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                score += 20;
            }

            return score;
        }

        private static bool IsPrivateIpv4(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        private async Task ClientLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConnected)
                {
                    try
                    {
                        await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    var discovery = _serviceDiscovery;
                    discovery?.QueryServiceInstances(ServiceTypeName);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Data service discovery query failed.");
                }

                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
            }
        }

        private async Task DirectConnectLoopAsync(string hostAddress, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConnected)
                {
                    try
                    {
                        await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    var receiveTask = await ConnectAsync(hostAddress, DataPort, token).ConfigureAwait(false);
                    if (receiveTask != null)
                    {
                        await receiveTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Data direct connect failed to {Host}:{Port}.", hostAddress, DataPort);
                }

                try
                {
                    await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            if (!IsMatchingServiceInstance(e.ServiceInstanceName))
            {
                return;
            }

            var tokenSource = _cts;
            if (tokenSource == null || tokenSource.IsCancellationRequested || IsConnected)
            {
                return;
            }

            if (!TryResolveEndpoint(e, out var address, out var port))
            {
                return;
            }

            _ = Task.Run(() => ConnectToDiscoveredServiceAsync(address, port, tokenSource.Token), tokenSource.Token);
        }

        private async Task ConnectToDiscoveredServiceAsync(IPAddress address, int port, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _connectionInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                if (token.IsCancellationRequested || IsConnected)
                {
                    return;
                }

                var receiveTask = await ConnectAsync(address.ToString(), port, token).ConfigureAwait(false);
                if (receiveTask != null)
                {
                    await receiveTask.ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _connectionInProgress, 0);
            }
        }

        private static bool IsMatchingServiceInstance(DomainName instanceName)
        {
            var name = instanceName.ToString().TrimEnd('.');
            if (!name.StartsWith(ServiceInstanceName + ".", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return name.IndexOf(ServiceType, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryResolveEndpoint(ServiceInstanceDiscoveryEventArgs e, out IPAddress address, out int port)
        {
            address = IPAddress.None;
            port = 0;

            var records = EnumerateRecords(e.Message).ToList();
            var srvRecord = records
                .OfType<SRVRecord>()
                .FirstOrDefault(record => DomainNameEquals(record.Name, e.ServiceInstanceName));

            DomainName? target = null;
            if (srvRecord != null)
            {
                port = srvRecord.Port;
                target = srvRecord.Target;
            }
            else
            {
                port = DataPort;
            }

            var addresses = new List<IPAddress>();
            if (target != null)
            {
                addresses.AddRange(records
                    .OfType<ARecord>()
                    .Where(record => DomainNameEquals(record.Name, target))
                    .Select(record => record.Address));

                addresses.AddRange(records
                    .OfType<AAAARecord>()
                    .Where(record => DomainNameEquals(record.Name, target))
                    .Select(record => record.Address));
            }

            if (addresses.Count == 0 && e.RemoteEndPoint != null)
            {
                addresses.Add(e.RemoteEndPoint.Address);
            }

            var selected = SelectAddress(addresses);
            if (selected == null || port <= 0)
            {
                return false;
            }

            address = selected;
            return true;
        }

        private static IEnumerable<ResourceRecord> EnumerateRecords(Message message)
        {
            foreach (var record in message.Answers)
            {
                yield return record;
            }

            foreach (var record in message.AdditionalRecords)
            {
                yield return record;
            }

            foreach (var record in message.AuthorityRecords)
            {
                yield return record;
            }
        }

        private static bool DomainNameEquals(DomainName? left, DomainName? right)
        {
            return left != null && right != null && left.Equals(right);
        }

        private static IPAddress? SelectAddress(IEnumerable<IPAddress> addresses)
        {
            var addressList = addresses as IList<IPAddress> ?? addresses.ToList();
            return addressList.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                ?? addressList.FirstOrDefault();
        }

        private async Task<Task?> ConnectAsync(string ip, int port, CancellationToken token)
        {
            var client = new TcpClient { NoDelay = false };

            try
            {
                await client.ConnectAsync(ip, port, token).ConfigureAwait(false);
                AddClient(Guid.Empty, client);

                if (!_clients.TryGetValue(Guid.Empty, out var connection))
                {
                    client.Close();
                    return null;
                }

                var receiveTask = ReceiveLoopAsync(Guid.Empty, connection, token);
                Log.Information("Data TCP client connected to {Ip}:{Port}.", ip, port);
                return receiveTask;
            }
            catch (OperationCanceledException)
            {
                Log.Information("Data TCP client connection canceled.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Data TCP client failed to connect to {Ip}:{Port}.", ip, port);
            }

            client.Close();
            return null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    client.NoDelay = false;
                    var clientId = Guid.NewGuid();

                    AddClient(clientId, client);
                    _ = ReceiveLoopAsync(clientId, _clients[clientId], token);

                    Log.Information("Data TCP client connected: {ClientId}.", clientId);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("Data TCP accept loop canceled.");
            }
            catch (ObjectDisposedException)
            {
                Log.Information("Data TCP listener disposed.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Data TCP accept loop failed.");
            }
        }

        private async Task ReceiveLoopAsync(Guid clientId, ClientConnection connection, CancellationToken token)
        {
            try
            {
                var lengthBuffer = new byte[sizeof(int)];

                while (!token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(connection.Stream, lengthBuffer, token).ConfigureAwait(false))
                    {
                        RemoveClient(clientId, "Remote connection closed.", expectedConnection: connection);
                        return;
                    }

                    int length = BitConverter.ToInt32(lengthBuffer, 0);
                    if (length <= 0)
                    {
                        RemoveClient(clientId, "Invalid message length received.", expectedConnection: connection);
                        return;
                    }

                    var payload = new byte[length];
                    if (!await ReadExactAsync(connection.Stream, payload, token).ConfigureAwait(false))
                    {
                        RemoveClient(clientId, "Remote connection closed while reading payload.", expectedConnection: connection);
                        return;
                    }

                    object? message;
                    try
                    {
                        message = MessagePackSerializer.Deserialize<object>(payload, SerializerOptions);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to deserialize incoming data message from {ClientId}.", clientId);
                        continue;
                    }

                    if (message != null)
                    {
                        MessageReceived?.Invoke(this, (message, clientId));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("Data TCP receive loop canceled for {ClientId}.", clientId);
            }
            catch (IOException ex)
            {
                RemoveClient(clientId, "Data TCP receive loop aborted due to IO error.", ex, connection);
            }
            catch (SocketException ex)
            {
                RemoveClient(clientId, "Data TCP receive loop aborted due to socket error.", ex, connection);
            }
            catch (Exception ex)
            {
                RemoveClient(clientId, "Unexpected error in data TCP receive loop.", ex, connection);
            }
        }

        private async Task SendToConnectionAsync(Guid clientId, ClientConnection connection, byte[] payload)
        {
            var lengthBytes = BitConverter.GetBytes(payload.Length);

            await connection.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await connection.Stream.WriteAsync(lengthBytes, 0, lengthBytes.Length).ConfigureAwait(false);
                await connection.Stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                await connection.Stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RemoveClient(clientId, "Failed to send data message.", ex, connection);
            }
            finally
            {
                connection.WriteLock.Release();
            }
        }

        private void AddClient(Guid clientId, TcpClient client)
        {
            var connection = new ClientConnection(client);
            if (_clients.TryAdd(clientId, connection))
            {
                UpdateConnectionState();
                ClientConnected?.Invoke(this, clientId);
                return;
            }

            Log.Warning("Data TCP client {ClientId} already tracked. Closing duplicate connection.", clientId);
            try
            {
                connection.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warning(disposeEx, "Error while disposing duplicate data TCP client {ClientId}.", clientId);
            }
        }

        private void RemoveClient(Guid clientId, string message, Exception? ex = null, ClientConnection? expectedConnection = null)
        {
            ClientConnection? connection;
            if (expectedConnection != null)
            {
                if (!_clients.TryGetValue(clientId, out connection) || !ReferenceEquals(connection, expectedConnection))
                {
                    return;
                }

                if (!_clients.TryRemove(new KeyValuePair<Guid, ClientConnection>(clientId, connection)))
                {
                    return;
                }
            }
            else if (!_clients.TryRemove(clientId, out connection))
            {
                return;
            }

            if (ex == null)
            {
                Log.Warning("{Message} ClientId={ClientId}", message, clientId);
            }
            else
            {
                Log.Warning(ex, "{Message} ClientId={ClientId}", message, clientId);
            }

            try
            {
                connection.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warning(disposeEx, "Error while disposing data TCP client {ClientId}.", clientId);
            }

            UpdateConnectionState();
            ClientDisconnected?.Invoke(this, clientId);
        }

        private void UpdateConnectionState()
        {
            _isConnected = !_clients.IsEmpty;
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
    }
}
