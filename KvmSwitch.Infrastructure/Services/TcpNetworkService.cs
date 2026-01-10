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
using System.Runtime.Versioning;
using KvmSwitch.Core.Interfaces;
using Makaretu.Dns;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Win32;
using Serilog;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class TcpNetworkService : INetworkService
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
        private const string ServiceType = "_kvmswitch._tcp";
        private const string ServiceInstanceName = "kvmcontroller";
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan BackoffMinDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan BackoffMaxDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(6);
        private const string PingMessage = "Ping";
        private const string PongMessage = "Pong";
        private static readonly DomainName ServiceTypeName = new(ServiceType);

        private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastPong = new();
        private readonly object _backoffSync = new();
        private readonly object _sync = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private Task? _clientLoopTask;
        private Task? _heartbeatTask;
        private CancellationTokenSource? _reconnectDelayCts;
        private ServiceDiscovery? _serviceDiscovery;
        private ServiceProfile? _serviceProfile;
        private int _connectionInProgress;
        private volatile bool _isConnected;
        private volatile bool _isServer;
        private bool _powerEventsSubscribed;
        private TimeSpan _currentBackoff = BackoffMinDelay;
        private string? _lastClientHost;

        public event EventHandler<(object Message, Guid ClientId)>? MessageReceived;
        public event EventHandler<Guid>? ClientConnected;
        public event EventHandler<Guid>? ClientDisconnected;

        public int Port { get; set; } = 65432;
        public bool IsConnected => _isConnected;
        public bool IsServer => _isServer;

        public Task StartServerAsync()
        {
            lock (_sync)
            {
                if (_listener != null)
                {
                    if (IsListenerActive(_listener))
                    {
                        Log.Warning("TCP server already running.");
                        return Task.CompletedTask;
                    }

                    if (!_clients.IsEmpty)
                    {
                        Log.Warning("TCP service already has active connections.");
                        return Task.CompletedTask;
                    }

                    ResetServerState();
                }

                if (!_clients.IsEmpty)
                {
                    Log.Warning("TCP service already has active connections.");
                    return Task.CompletedTask;
                }

                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                try
                {
                    _listener.Start();
                }
                catch
                {
                    ResetServerState();
                    throw;
                }
                _isServer = true;
                _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
                StartServiceAdvertisement();
                EnsurePowerEventsSubscribed();
                StartHeartbeatLoop();
            }

            Log.Information("TCP server listening on port {Port}.", Port);
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
                    Log.Warning("TCP service is already running as a server.");
                    return Task.CompletedTask;
                }

                if (_cts != null || !_clients.IsEmpty)
                {
                    Log.Warning("TCP client already running.");
                    return Task.CompletedTask;
                }

                _cts = new CancellationTokenSource();
                _isServer = false;
                _lastClientHost = string.IsNullOrWhiteSpace(hostAddress) ? null : hostAddress.Trim();
                EnsurePowerEventsSubscribed();
                StartHeartbeatLoop();

                if (_lastClientHost == null)
                {
                    _serviceDiscovery = new ServiceDiscovery();
                    _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
                    try
                    {
                        _serviceDiscovery.QueryServiceInstances(ServiceTypeName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Service discovery query failed.");
                    }

                    _clientLoopTask = Task.Run(() => ClientLoopAsync(_cts.Token));
                    Log.Information("TCP client auto-discovery started.");
                    return Task.CompletedTask;
                }

                var targetHost = _lastClientHost!;
                _clientLoopTask = Task.Run(() => DirectConnectLoopAsync(targetHost, _cts.Token));
                Log.Information("TCP client direct connect started to {Host}:{Port}.", targetHost, Port);
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
                Log.Error(ex, "Failed to serialize message of type {MessageType}.", message.GetType().FullName);
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
            CancellationTokenSource? reconnectDelayCts;

            lock (_sync)
            {
                cts = _cts;
                _cts = null;
                listener = _listener;
                _listener = null;
                _acceptTask = null;
                _clientLoopTask = null;
                _heartbeatTask = null;
                reconnectDelayCts = _reconnectDelayCts;
                _reconnectDelayCts = null;
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
                    Log.Warning(ex, "Error while stopping service discovery.");
                }
            }

            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping TCP service.");
            }

            try
            {
                reconnectDelayCts?.Cancel();
                reconnectDelayCts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping reconnect delay.");
            }

            try
            {
                listener?.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping TCP listener.");
            }

            foreach (var clientId in _clients.Keys)
            {
                RemoveClient(clientId, "TCP service stopped.");
            }

            UpdateConnectionState();
            UnsubscribePowerEvents();
            Log.Information("TCP network service stopped.");
        }

        private void ResetServerState()
        {
            var cts = _cts;
            _cts = null;
            _listener = null;
            _acceptTask = null;
            _isServer = false;

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while canceling TCP service startup.");
            }

            cts.Dispose();
        }

        private void EnsurePowerEventsSubscribed()
        {
            if (!OperatingSystem.IsWindows() || _powerEventsSubscribed)
            {
                return;
            }

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerEventsSubscribed = true;
        }

        private void UnsubscribePowerEvents()
        {
            if (!OperatingSystem.IsWindows() || !_powerEventsSubscribed)
            {
                return;
            }

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _powerEventsSubscribed = false;
        }

        [SupportedOSPlatform("windows")]
        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Log.Information("System suspend detected. Closing active connections.");
                HandleSuspend();
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                Log.Information("System resume detected. Triggering reconnect.");
                HandleResume();
            }
        }

        private void HandleSuspend()
        {
            foreach (var clientId in _clients.Keys)
            {
                RemoveClient(clientId, "System suspend.");
            }

            UpdateConnectionState();
        }

        private void HandleResume()
        {
            ResetBackoff();
            CancelReconnectDelay();

            if (_serviceDiscovery != null && !_isServer)
            {
                try
                {
                    _serviceDiscovery.QueryServiceInstances(ServiceTypeName);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Service discovery query failed after resume.");
                }
            }
        }

        private void StartHeartbeatLoop()
        {
            if (_cts == null || _heartbeatTask != null)
            {
                return;
            }

            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_clients.IsEmpty)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                foreach (var clientId in _clients.Keys)
                {
                    if (_lastPong.TryGetValue(clientId, out var lastSeen)
                        && now - lastSeen > HeartbeatTimeout)
                    {
                        RemoveClient(clientId, "Heartbeat timeout.");
                        continue;
                    }

                    _ = SendAsync(PingMessage, clientId);
                }
            }
        }

        private void TouchHeartbeat(Guid clientId)
        {
            _lastPong[clientId] = DateTime.UtcNow;
        }

        private bool TryHandleHeartbeatMessage(Guid clientId, object message)
        {
            if (message is not string text)
            {
                return false;
            }

            if (string.Equals(text, PingMessage, StringComparison.OrdinalIgnoreCase))
            {
                TouchHeartbeat(clientId);
                _ = SendAsync(PongMessage, clientId);
                return true;
            }

            if (string.Equals(text, PongMessage, StringComparison.OrdinalIgnoreCase))
            {
                TouchHeartbeat(clientId);
                return true;
            }

            return false;
        }

        private void ResetBackoff()
        {
            lock (_backoffSync)
            {
                _currentBackoff = BackoffMinDelay;
            }
        }

        private void IncreaseBackoff()
        {
            lock (_backoffSync)
            {
                var nextSeconds = Math.Min(_currentBackoff.TotalSeconds * 2, BackoffMaxDelay.TotalSeconds);
                _currentBackoff = TimeSpan.FromSeconds(nextSeconds);
            }
        }

        private TimeSpan GetBackoffDelay()
        {
            lock (_backoffSync)
            {
                return _currentBackoff;
            }
        }

        private async Task DelayWithBackoffAsync(CancellationToken token)
        {
            var delay = GetBackoffDelay();
            CancellationTokenSource delayCts;

            lock (_sync)
            {
                _reconnectDelayCts?.Cancel();
                _reconnectDelayCts?.Dispose();
                _reconnectDelayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                delayCts = _reconnectDelayCts;
            }

            try
            {
                await Task.Delay(delay, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_reconnectDelayCts, delayCts))
                    {
                        _reconnectDelayCts.Dispose();
                        _reconnectDelayCts = null;
                    }
                }
            }
        }

        private void CancelReconnectDelay()
        {
            lock (_sync)
            {
                _reconnectDelayCts?.Cancel();
            }
        }

        private static bool IsListenerActive(TcpListener listener)
        {
            try
            {
                return listener.Server.IsBound;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
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
                    (ushort)Port,
                    GetLocalAddresses());

                _serviceDiscovery = new ServiceDiscovery();
                _serviceDiscovery.Advertise(_serviceProfile);
                Log.Information("mDNS service advertised as {Name} on port {Port}.", ServiceInstanceName, Port);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to advertise mDNS service.");
            }
        }

        private static IReadOnlyList<IPAddress> GetLocalAddresses()
        {
            var overrideIp = Environment.GetEnvironmentVariable("KVM_SWITCH_ADVERTISE_IP");
            if (!string.IsNullOrWhiteSpace(overrideIp) && IPAddress.TryParse(overrideIp, out var parsedIp))
            {
                Log.Information("Using overridden advertise IP: {Ip}.", parsedIp);
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
            var selected = ordered[0];

            if (ordered.Count > 1)
            {
                var candidateSummary = string.Join(", ", ordered.Select(candidate =>
                    $"{candidate.Ip} ({candidate.Nic.Name}) score={candidate.Score}"));
                Log.Information("Candidate advertise IPs: {Candidates}.", candidateSummary);
            }

            Log.Information("Selected advertise IP {Ip} from {Interface} ({Description}).",
                selected.Ip, selected.Nic.Name, selected.Nic.Description);
            return new[] { selected.Ip };
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
                    if (discovery != null)
                    {
                        discovery.QueryServiceInstances(ServiceTypeName);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Service discovery query failed.");
                }

                await DelayWithBackoffAsync(token).ConfigureAwait(false);
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
                    var receiveTask = await ConnectAsync(hostAddress, Port, token).ConfigureAwait(false);
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
                    Log.Warning(ex, "Direct connect failed to {Host}:{Port}.", hostAddress, Port);
                }

                try
                {
                    await DelayWithBackoffAsync(token).ConfigureAwait(false);
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
                port = Port;
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
            var client = new TcpClient { NoDelay = true };

            try
            {
                await client.ConnectAsync(ip, port, token).ConfigureAwait(false);
                AddClient(Guid.Empty, client);
                ResetBackoff();

                if (!_clients.TryGetValue(Guid.Empty, out var connection))
                {
                    client.Close();
                    return null;
                }

                var receiveTask = ReceiveLoopAsync(Guid.Empty, connection, token);
                Log.Information("TCP client connected to {Ip}:{Port}.", ip, port);
                return receiveTask;
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP client connection canceled.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TCP client failed to connect to {Ip}:{Port}.", ip, port);
            }

            IncreaseBackoff();
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
                    client.NoDelay = true;
                    var clientId = Guid.NewGuid();

                    AddClient(clientId, client);
                    _ = ReceiveLoopAsync(clientId, _clients[clientId], token);

                    Log.Information("TCP client connected: {ClientId}.", clientId);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP accept loop canceled.");
            }
            catch (ObjectDisposedException)
            {
                Log.Information("TCP listener disposed.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TCP accept loop failed.");
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
                        Log.Error(ex, "Failed to deserialize incoming message from {ClientId}.", clientId);
                        continue;
                    }

                    if (message != null)
                    {
                        if (TryHandleHeartbeatMessage(clientId, message))
                        {
                            continue;
                        }

                        TouchHeartbeat(clientId);
                        MessageReceived?.Invoke(this, (message, clientId));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("TCP receive loop canceled for {ClientId}.", clientId);
            }
            catch (IOException ex)
            {
                RemoveClient(clientId, "TCP receive loop aborted due to IO error.", ex, connection);
            }
            catch (SocketException ex)
            {
                RemoveClient(clientId, "TCP receive loop aborted due to socket error.", ex, connection);
            }
            catch (Exception ex)
            {
                RemoveClient(clientId, "Unexpected error in TCP receive loop.", ex, connection);
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
                RemoveClient(clientId, "Failed to send message.", ex, connection);
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
                TouchHeartbeat(clientId);
                ResetBackoff();
                StartHeartbeatLoop();
                UpdateConnectionState();
                ClientConnected?.Invoke(this, clientId);
                return;
            }

            Log.Warning("TCP client {ClientId} already tracked. Closing duplicate connection.", clientId);
            try
            {
                connection.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warning(disposeEx, "Error while disposing duplicate TCP client {ClientId}.", clientId);
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

            _lastPong.TryRemove(clientId, out _);
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
                Log.Warning(disposeEx, "Error while disposing TCP client {ClientId}.", clientId);
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
