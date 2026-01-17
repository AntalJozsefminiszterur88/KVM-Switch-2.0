using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace KvmSwitch.Infrastructure.Services;

public sealed class FileTransferService : IFileTransferService
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

    private sealed class FileSendEntry
    {
        public FileSendEntry(string path, string fileName, byte[] fileNameBytes, long size)
        {
            Path = path;
            FileName = fileName;
            FileNameBytes = fileNameBytes;
            Size = size;
        }

        public string Path { get; }
        public string FileName { get; }
        public byte[] FileNameBytes { get; }
        public long Size { get; }
    }

    private const int TransferPort = 6001;
    private const int BufferSize = 256 * 1024;
    private const int MaxFileNameBytes = 4096;
    private const long MaxFileBytes = 1L * 1024 * 1024 * 1024;
    private const string ServiceType = "_kvmswitch-file._tcp";
    private const string ServiceInstanceName = "kvmfiles";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FileSendTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(2);
    private static readonly TimeSpan CleanupMaxAge = TimeSpan.FromDays(2);
    private static readonly DomainName ServiceTypeName = new(ServiceType);

    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly object _sync = new();
    private readonly ILogger<FileTransferService> _logger;
    private readonly string _receiveDirectory;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _clientLoopTask;
    private Task? _keepAliveTask;
    private Task? _cleanupTask;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _serviceProfile;
    private int _connectionInProgress;
    private volatile bool _isConnected;
    private volatile bool _isServer;
    private bool _powerEventsSubscribed;

    public event EventHandler<IReadOnlyList<string>>? FilesReceived;

    public bool IsConnected => _isConnected;
    public bool IsServer => _isServer;

    public FileTransferService(ILogger<FileTransferService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _receiveDirectory = Path.Combine(Path.GetTempPath(), "KvmSwitch_Recv");
        CleanupReceiveDirectory();
    }

    public Task StartServerAsync()
    {
        lock (_sync)
        {
            if (_listener != null)
            {
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, TransferPort);
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
            EnsurePowerEventsSubscribed();
            StartKeepAliveLoop();
            StartCleanupLoop();
        }

        _ = TryOpenFirewallPortAsync(TransferPort);
        _logger.LogInformation("File transfer server listening on port {Port}.", TransferPort);
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
                _logger.LogWarning("File transfer service is already running as a server.");
                return Task.CompletedTask;
            }

            if (_cts != null || !_clients.IsEmpty)
            {
                _logger.LogWarning("File transfer client already running.");
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            _isServer = false;
            EnsurePowerEventsSubscribed();
            StartKeepAliveLoop();
            StartCleanupLoop();

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
                    _logger.LogWarning(ex, "File transfer service discovery query failed.");
                }

                _clientLoopTask = Task.Run(() => ClientLoopAsync(_cts.Token));
                _logger.LogInformation("File transfer client auto-discovery started.");
                return Task.CompletedTask;
            }

            var targetHost = hostAddress.Trim();
            _clientLoopTask = Task.Run(() => DirectConnectLoopAsync(targetHost, _cts.Token));
            _logger.LogInformation("File transfer client direct connect started to {Host}:{Port}.", targetHost, TransferPort);
        }

        return Task.CompletedTask;
    }

    public async Task SendFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths == null)
        {
            throw new ArgumentNullException(nameof(filePaths));
        }

        var files = PrepareFilesForSend(filePaths);
        if (files.Count == 0)
        {
            _logger.LogDebug("No valid files to send for file transfer.");
            return;
        }

        if (_clients.IsEmpty)
        {
            _logger.LogWarning("File transfer send requested but no active connections are available.");
            throw new InvalidOperationException("No file transfer connection available.");
        }

        var totalBytes = 0L;
        foreach (var file in files)
        {
            totalBytes += file.Size;
        }

        var clients = _clients.ToArray();
        _logger.LogInformation(
            "Sending {Count} file(s) ({TotalBytes} bytes) to {ClientCount} connection(s).",
            files.Count,
            totalBytes,
            clients.Length);
        _logger.LogDebug("Files queued for transfer: {Files}", FormatFileNames(files, 6));

        var sendTasks = new List<Task<SendResult>>(clients.Length);
        foreach (var kvp in clients)
        {
            sendTasks.Add(SendFilesToClientAsync(kvp.Key, kvp.Value, files, cancellationToken));
        }

        var results = await Task.WhenAll(sendTasks).ConfigureAwait(false);
        var failures = new List<Exception>();
        var successCount = 0;
        foreach (var result in results)
        {
            if (result.Success)
            {
                successCount++;
                continue;
            }

            if (result.Exception != null)
            {
                failures.Add(result.Exception);
            }
        }

        if (failures.Count > 0)
        {
            if (successCount == 0)
            {
                throw new AggregateException("Failed to send files to any connection.", failures);
            }

            _logger.LogWarning("File transfer completed with {FailureCount} failed connection(s).", failures.Count);
        }
    }

    private readonly struct SendResult
    {
        public SendResult(Exception? exception)
        {
            Exception = exception;
        }

        public Exception? Exception { get; }
        public bool Success => Exception == null;
    }

    private async Task<SendResult> SendFilesToClientAsync(
        Guid clientId,
        ClientConnection connection,
        IReadOnlyList<FileSendEntry> files,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FileSendTimeout);

        try
        {
            await SendFilesToConnectionAsync(clientId, connection, files, timeoutCts.Token).ConfigureAwait(false);
            return new SendResult(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException($"File transfer send timed out after {FileSendTimeout}.");
            RemoveClient(clientId, "File transfer send timed out.", timeoutException, connection);
            return new SendResult(timeoutException);
        }
        catch (Exception ex)
        {
            return new SendResult(ex);
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
            _keepAliveTask = null;
            _cleanupTask = null;
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
                _logger.LogWarning(ex, "Error while stopping file transfer service discovery.");
            }
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping file transfer service.");
        }

        try
        {
            listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping file transfer listener.");
        }

        foreach (var clientId in _clients.Keys)
        {
            RemoveClient(clientId, "File transfer service stopped.");
        }

        UpdateConnectionState();
        UnsubscribePowerEvents();
        CleanupReceiveDirectory();
        _logger.LogInformation("File transfer service stopped.");
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
            _logger.LogInformation("System suspend detected. Closing file transfer connections.");
            HandleSuspend();
            return;
        }

        if (e.Mode == PowerModes.Resume)
        {
            _logger.LogInformation("System resume detected. Triggering file transfer reconnect.");
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
        if (_serviceDiscovery != null && !_isServer)
        {
            try
            {
                _serviceDiscovery.QueryServiceInstances(ServiceTypeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File transfer discovery query failed after resume.");
            }
        }
    }

    private void StartKeepAliveLoop()
    {
        if (_cts == null || _keepAliveTask != null)
        {
            return;
        }

        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
    }

    private async Task KeepAliveLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (_clients.IsEmpty)
            {
                continue;
            }

            foreach (var kvp in _clients.ToArray())
            {
                await TrySendKeepAliveAsync(kvp.Key, kvp.Value, token).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySendKeepAliveAsync(Guid clientId, ClientConnection connection, CancellationToken token)
    {
        if (!connection.WriteLock.Wait(0))
        {
            return;
        }

        try
        {
            await WriteBatchEndAsync(connection.Stream, token).ConfigureAwait(false);
            await connection.Stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ShouldRemoveClient(ex))
            {
                RemoveClient(clientId, "File transfer keepalive failed.", ex, connection);
            }
        }
        finally
        {
            try
            {
                connection.WriteLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Connection already disposed.
            }
        }
    }

    private void StartCleanupLoop()
    {
        if (_cts == null || _cleanupTask != null)
        {
            return;
        }

        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token));
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        CleanupReceiveDirectory(CleanupMaxAge);
        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            CleanupReceiveDirectory(CleanupMaxAge);
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
                (ushort)TransferPort,
                GetLocalAddresses());

            _serviceDiscovery = new ServiceDiscovery();
            _serviceDiscovery.Advertise(_serviceProfile);
            _logger.LogInformation("File transfer mDNS service advertised as {Name} on port {Port}.", ServiceInstanceName, TransferPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to advertise file transfer service.");
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
                _logger.LogWarning(ex, "File transfer discovery query failed.");
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
                var receiveTask = await ConnectAsync(hostAddress, TransferPort, token).ConfigureAwait(false);
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
                _logger.LogWarning(ex, "File transfer direct connect failed to {Host}:{Port}.", hostAddress, TransferPort);
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
            port = TransferPort;
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
            _logger.LogInformation("File transfer client connected to {Ip}:{Port}.", ip, port);
            return receiveTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File transfer client connection canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File transfer client failed to connect to {Ip}:{Port}.", ip, port);
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

                _logger.LogInformation("File transfer client connected: {ClientId}.", clientId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File transfer accept loop canceled.");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogInformation("File transfer listener disposed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File transfer accept loop failed.");
        }
    }

    private async Task ReceiveLoopAsync(Guid clientId, ClientConnection connection, CancellationToken token)
    {
        var receivedFiles = new List<string>();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var lengthBuffer = new byte[sizeof(int)];
        var sizeBuffer = new byte[sizeof(long)];
        string? inFlightTempPath = null;
        string? removeMessage = null;
        Exception? removeException = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(connection.Stream, lengthBuffer, token).ConfigureAwait(false))
                {
                    removeMessage = "Remote file transfer connection closed.";
                    break;
                }

                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (nameLength == 0)
                {
                    if (!await ReadExactAsync(connection.Stream, sizeBuffer, token).ConfigureAwait(false))
                    {
                        removeMessage = "Remote connection closed while reading batch marker.";
                        break;
                    }

                    if (receivedFiles.Count > 0)
                    {
                        _logger.LogDebug("File transfer batch received from {ClientId} ({Count} files).", clientId, receivedFiles.Count);
                        FilesReceived?.Invoke(this, receivedFiles.ToArray());
                        receivedFiles.Clear();
                    }

                    continue;
                }

                if (nameLength < 0 || nameLength > MaxFileNameBytes)
                {
                    removeMessage = $"Invalid file name length received ({nameLength}).";
                    break;
                }

                var nameBytes = new byte[nameLength];
                if (!await ReadExactAsync(connection.Stream, nameBytes, token).ConfigureAwait(false))
                {
                    removeMessage = "Remote connection closed while reading file name.";
                    break;
                }

                if (!await ReadExactAsync(connection.Stream, sizeBuffer, token).ConfigureAwait(false))
                {
                    removeMessage = "Remote connection closed while reading file size.";
                    break;
                }

                var size = BinaryPrimitives.ReadInt64LittleEndian(sizeBuffer);
                if (size < 0 || size > MaxFileBytes)
                {
                    removeMessage = $"Incoming file exceeds size limits ({size} bytes).";
                    break;
                }

                var originalName = Encoding.UTF8.GetString(nameBytes);
                var safeName = Path.GetFileName(originalName);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = "received_file";
                }

                EnsureReceiveDirectory();
                var finalPath = GetUniqueFilePath(_receiveDirectory, safeName);
                var tempPath = finalPath + ".part";
                inFlightTempPath = tempPath;

                _logger.LogDebug("Receiving file {FileName} ({Size} bytes) from {ClientId}.", safeName, size, clientId);
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
                {
                    var remaining = size;
                    while (remaining > 0)
                    {
                        var readSize = (int)Math.Min(remaining, buffer.Length);
                        var read = await connection.Stream.ReadAsync(buffer, 0, readSize, token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            removeMessage = "Remote connection closed during file payload.";
                            break;
                        }

                        await fileStream.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        remaining -= read;
                    }

                    await fileStream.FlushAsync(token).ConfigureAwait(false);

                    if (remaining > 0)
                    {
                        break;
                    }
                }

                File.Move(tempPath, finalPath, overwrite: true);
                inFlightTempPath = null;
                _logger.LogDebug("Received file saved to {Path}.", finalPath);
                receivedFiles.Add(finalPath);
            }
        }
        catch (OperationCanceledException)
        {
            removeMessage = null;
        }
        catch (IOException ex)
        {
            removeMessage = "File transfer receive loop IO error.";
            removeException = ex;
        }
        catch (SocketException ex)
        {
            removeMessage = "File transfer receive loop socket error.";
            removeException = ex;
        }
        catch (Exception ex)
        {
            removeMessage = "Unexpected error in file transfer receive loop.";
            removeException = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (inFlightTempPath != null)
            {
                SafeDelete(inFlightTempPath);
            }

            if (receivedFiles.Count > 0)
            {
                FilesReceived?.Invoke(this, receivedFiles.ToArray());
            }

            if (removeMessage != null)
            {
                RemoveClient(clientId, removeMessage, removeException, connection);
            }
        }
    }

    private async Task SendFilesToConnectionAsync(
        Guid clientId,
        ClientConnection connection,
        IReadOnlyList<FileSendEntry> files,
        CancellationToken token)
    {
        await connection.WriteLock.WaitAsync(token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        Exception? sendException = null;
        try
        {
            var sentAny = false;
            foreach (var file in files)
            {
                FileStream? fileStream;
                try
                {
                    fileStream = new FileStream(
                        file.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        BufferSize,
                        FileOptions.SequentialScan);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Skipping file transfer for {Path}.", file.Path);
                    continue;
                }

                await using (fileStream)
                {
                    var size = fileStream.Length;
                    if (size < 0 || size > MaxFileBytes)
                    {
                        _logger.LogWarning("Skipping file transfer for {Path} due to invalid size {Size}.", file.Path, size);
                        continue;
                    }

                    _logger.LogDebug("Sending file {FileName} ({Size} bytes) to {ClientId}.", file.FileName, size, clientId);
                    await WriteHeaderAsync(connection.Stream, file.FileNameBytes, size, token).ConfigureAwait(false);
                    await CopyStreamAsync(fileStream, connection.Stream, buffer, size, token).ConfigureAwait(false);
                    _logger.LogDebug("Sent file {FileName} to {ClientId}.", file.FileName, clientId);
                    sentAny = true;
                }
            }

            if (!sentAny)
            {
                return;
            }

            await WriteBatchEndAsync(connection.Stream, token).ConfigureAwait(false);
            await connection.Stream.FlushAsync(token).ConfigureAwait(false);
            _logger.LogDebug("File transfer batch sent to {ClientId} ({Count} files).", clientId, files.Count);
        }
        catch (Exception ex)
        {
            sendException = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                connection.WriteLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Connection already disposed.
            }
        }

        if (sendException != null)
        {
            if (ShouldRemoveClient(sendException))
            {
                RemoveClient(clientId, "Failed to send file transfer payload.", sendException, connection);
            }

            ExceptionDispatchInfo.Capture(sendException).Throw();
        }
    }

    private static async Task CopyStreamAsync(
        Stream source,
        Stream destination,
        byte[] buffer,
        long bytesToCopy,
        CancellationToken token)
    {
        var remaining = bytesToCopy;
        while (remaining > 0)
        {
            var readSize = (int)Math.Min(remaining, buffer.Length);
            var read = await source.ReadAsync(buffer, 0, readSize, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("File content ended before expected size was read.");
            }

            await destination.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task WriteHeaderAsync(NetworkStream stream, byte[] fileNameBytes, long size, CancellationToken token)
    {
        var nameLengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(nameLengthBytes, fileNameBytes.Length);
        await stream.WriteAsync(nameLengthBytes, 0, nameLengthBytes.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(fileNameBytes, 0, fileNameBytes.Length, token).ConfigureAwait(false);
        var sizeBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, size);
        await stream.WriteAsync(sizeBytes, 0, sizeBytes.Length, token).ConfigureAwait(false);
    }

    private async Task TryOpenFirewallPortAsync(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0 || !IsRunningAsAdministrator())
        {
            return;
        }

        var ruleName = $"KVM Switch File Transfer {port}";
        var command =
            $"if (-not (Get-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue)) " +
            $"{{ New-NetFirewallRule -DisplayName '{ruleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} | Out-Null }}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start PowerShell process for file transfer firewall rule.");
                return;
            }

            _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stdErr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Ensured firewall rule for file transfer port {Port}.", port);
            }
            else
            {
                _logger.LogWarning("File transfer firewall rule command failed (exit {ExitCode}). {Error}", process.ExitCode, stdErr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure firewall rule for file transfer port {Port}.", port);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task WriteBatchEndAsync(NetworkStream stream, CancellationToken token)
    {
        var nameLengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(nameLengthBytes, 0);
        await stream.WriteAsync(nameLengthBytes, 0, nameLengthBytes.Length, token).ConfigureAwait(false);
        var sizeBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, 0);
        await stream.WriteAsync(sizeBytes, 0, sizeBytes.Length, token).ConfigureAwait(false);
    }

    private IReadOnlyList<FileSendEntry> PrepareFilesForSend(IEnumerable<string> filePaths)
    {
        var files = new List<FileSendEntry>();
        long totalBytes = 0;

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                _logger.LogWarning("File not found for transfer: {Path}.", path);
                continue;
            }

            var info = new FileInfo(path);
            var size = info.Length;
            if (size < 0)
            {
                continue;
            }

            totalBytes += size;
            if (totalBytes > MaxFileBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(filePaths), "Total file size exceeds transfer limit.");
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var nameBytes = Encoding.UTF8.GetBytes(fileName);
            if (nameBytes.Length > MaxFileNameBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(filePaths), "File name too long for transfer protocol.");
            }

            files.Add(new FileSendEntry(path, fileName, nameBytes, size));
        }

        return files;
    }

    private void AddClient(Guid clientId, TcpClient client)
    {
        var connection = new ClientConnection(client);
        if (_clients.TryAdd(clientId, connection))
        {
            UpdateConnectionState();
            return;
        }

        _logger.LogWarning("File transfer client {ClientId} already tracked. Closing duplicate connection.", clientId);
        try
        {
            connection.Dispose();
        }
        catch (Exception disposeEx)
        {
            _logger.LogWarning(disposeEx, "Error while disposing duplicate file transfer client {ClientId}.", clientId);
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
            _logger.LogWarning("{Message} ClientId={ClientId}", message, clientId);
        }
        else
        {
            _logger.LogWarning(ex, "{Message} ClientId={ClientId}", message, clientId);
        }

        try
        {
            connection.Dispose();
        }
        catch (Exception disposeEx)
        {
            _logger.LogWarning(disposeEx, "Error while disposing file transfer client {ClientId}.", clientId);
        }

        UpdateConnectionState();
    }

    private void UpdateConnectionState()
    {
        _isConnected = !_clients.IsEmpty;
    }

    private static bool ShouldRemoveClient(Exception exception)
    {
        return exception is IOException or SocketException or ObjectDisposedException or EndOfStreamException;
    }

    private static string FormatFileNames(IReadOnlyList<FileSendEntry> files, int maxItems)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var take = Math.Min(files.Count, maxItems);
        var preview = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            var name = files[i].FileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                preview.Add(name);
            }
        }

        var summary = string.Join(", ", preview);
        if (files.Count > maxItems)
        {
            summary = $"{summary} (+{files.Count - maxItems} more)";
        }

        return summary;
    }

    private void EnsureReceiveDirectory()
    {
        Directory.CreateDirectory(_receiveDirectory);
    }

    private void CleanupReceiveDirectory()
    {
        try
        {
            if (Directory.Exists(_receiveDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_receiveDirectory))
                {
                    SafeDelete(file);
                }

                foreach (var dir in Directory.EnumerateDirectories(_receiveDirectory))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            Directory.CreateDirectory(_receiveDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean file transfer receive directory.");
        }
    }

    private void CleanupReceiveDirectory(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(_receiveDirectory))
            {
                Directory.CreateDirectory(_receiveDirectory);
                return;
            }

            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(_receiveDirectory))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc <= cutoff)
                    {
                        SafeDelete(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect file transfer temp file {Path}.", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean file transfer receive directory.");
        }
    }

    private static string GetUniqueFilePath(string directory, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var counter = 1;

        while (File.Exists(candidate) || File.Exists(candidate + ".part"))
        {
            var nextName = $"{baseName} ({counter}){extension}";
            candidate = Path.Combine(directory, nextName);
            counter++;
        }

        return candidate;
    }

    private static bool SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
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
