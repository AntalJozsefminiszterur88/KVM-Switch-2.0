using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using Serilog;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class PicoSerialService : ISerialService
    {
        private const int DefaultBaudRate = 115200;
        private const int ReadTimeoutMs = 1000;
        private const int ReconnectDelayMs = 1000;

        private readonly object _sync = new();
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _readTask;
        private string? _portName;

        public event EventHandler<string>? CommandReceived;

        public void Start(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new ArgumentException("Port name is required.", nameof(portName));
            }

            CancellationToken token;
            lock (_sync)
            {
                if (_readTask != null)
                {
                    return;
                }

                _portName = portName;
                _cts = new CancellationTokenSource();
                token = _cts.Token;
                _readTask = Task.Run(() => ReadLoopAsync(token));
            }

            try
            {
                EnsurePortOpen();
                Log.Information("Serial service started on {PortName}.", portName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Serial port {PortName} unavailable. Retrying in background.", portName);
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;

            lock (_sync)
            {
                cts = _cts;
                _cts = null;
                _readTask = null;
            }

            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while stopping serial service.");
            }

            ClosePort();
            Log.Information("Serial service stopped.");
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                SerialPort? port;
                lock (_sync)
                {
                    port = _port;
                }

                if (port == null)
                {
                    try
                    {
                        EnsurePortOpen();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Serial port reconnect failed.");
                    }

                    await Task.Delay(ReconnectDelayMs, token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var line = port.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        CommandReceived?.Invoke(this, line.Trim());
                    }
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Serial read error.");
                    ClosePort();
                    await Task.Delay(ReconnectDelayMs, token).ConfigureAwait(false);
                }
            }
        }

        private void EnsurePortOpen()
        {
            lock (_sync)
            {
                if (_port != null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_portName))
                {
                    return;
                }

                var port = new SerialPort(_portName, DefaultBaudRate)
                {
                    NewLine = "\n",
                    ReadTimeout = ReadTimeoutMs
                };

                try
                {
                    port.Open();
                }
                catch
                {
                    port.Dispose();
                    throw;
                }
                _port = port;
            }
        }

        private void ClosePort()
        {
            SerialPort? port;

            lock (_sync)
            {
                port = _port;
                _port = null;
            }

            try
            {
                port?.Close();
                port?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while closing serial port.");
            }
        }
    }
}
