using System;
using System.Threading.Tasks;
using KvmSwitch.Core.Interfaces;
using KvmSwitch.Core.Models;
using Serilog;
using SharpHook;
using SharpHook.Data;

namespace KvmSwitch.Infrastructure.Services
{
    public sealed class SharpHookInputService : IInputService
    {
        private const double ScrollScale = 0.576;
        private readonly object _sync = new();
        private readonly EventSimulator _eventSimulator = new();
        private SimpleGlobalHook? _hook;
        private Task? _hookTask;
        private CancellationTokenSource? _hookCts;
        private volatile bool _suppressLocalInput = true;
        private int _centerX;
        private int _centerY;
        private int _lastX;
        private int _lastY;
        private bool _centerInitialized;
        private bool _isResettingPosition;
        private int? _boundsWidth;
        private int? _boundsHeight;
        private double _scrollRemainder;

        public event EventHandler<InputEvent>? InputReceived;

        public void SetPointerBounds(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                _boundsWidth = null;
                _boundsHeight = null;
                _centerInitialized = false;
                return;
            }

            _boundsWidth = width;
            _boundsHeight = height;
            InitializeCenterFromBounds();
            if (_suppressLocalInput)
            {
                LockPointerToCenter();
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_hookTask != null)
                {
                    return;
                }

                try
                {
                    _hookCts = new CancellationTokenSource();
                    _suppressLocalInput = true;
                    ResetCaptureState();
                    InitializeCenterFromBounds();
                    LockPointerToCenter();
                    _hookTask = Task.Run(() => RunHookLoopAsync(_hookCts.Token));

                    Log.Information("SharpHook input service started.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to start SharpHook input service.");
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_hookTask == null)
                {
                    return;
                }

                try
                {
                    _hookCts?.Cancel();
                    _hookCts?.Dispose();
                    _hookCts = null;
                    _hook?.Dispose();
                    _hook = null;
                    _hookTask = null;
                    _suppressLocalInput = false;
                    ResetCaptureState();
                    Log.Information("SharpHook input service stopped.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to stop SharpHook input service.");
                    throw;
                }
            }
        }

        private async Task RunHookLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                SimpleGlobalHook? hook = null;

                try
                {
                    hook = new SimpleGlobalHook();
                    hook.KeyPressed += OnKeyPressed;
                    hook.KeyReleased += OnKeyReleased;
                    hook.MouseMoved += OnMouseMoved;
                    hook.MouseDragged += OnMouseDragged;
                    hook.MousePressed += OnMousePressed;
                    hook.MouseReleased += OnMouseReleased;
                    hook.MouseWheel += OnMouseWheel;

                    lock (_sync)
                    {
                        _hook = hook;
                    }

                    ResetCaptureState();
                    InitializeCenterFromBounds();
                    LockPointerToCenter();

                    await hook.RunAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Input hook crashed, restarting...");
                }
                finally
                {
                    try
                    {
                        hook?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to dispose SharpHook instance.");
                    }

                    lock (_sync)
                    {
                        if (ReferenceEquals(_hook, hook))
                        {
                            _hook = null;
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        public void SimulateInput(InputEvent inputEvent)
        {
            try
            {
                switch (inputEvent.EventType)
                {
                    case InputEventType.KeyDown:
                        _eventSimulator.SimulateKeyPress((KeyCode)inputEvent.Key);
                        break;
                    case InputEventType.KeyUp:
                        _eventSimulator.SimulateKeyRelease((KeyCode)inputEvent.Key);
                        break;
                    case InputEventType.MouseMove:
                        _eventSimulator.SimulateMouseMovement((short)inputEvent.MouseX, (short)inputEvent.MouseY);
                        break;
                    case InputEventType.MouseDown:
                        _eventSimulator.SimulateMousePress((MouseButton)inputEvent.MouseButton);
                        break;
                    case InputEventType.MouseUp:
                        _eventSimulator.SimulateMouseRelease((MouseButton)inputEvent.MouseButton);
                        break;
                    case InputEventType.MouseWheel:
                        _eventSimulator.SimulateMouseWheel(
                            (short)inputEvent.MouseButton,
                            MouseWheelScrollDirection.Vertical,
                            MouseWheelScrollType.UnitScroll);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to simulate input event {EventType}.", inputEvent.EventType);
                throw;
            }
        }

        private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;
                if (e.IsEventSimulated)
                {
                    return;
                }
            }
            else
            {
                SuppressIfNeeded(e);
            }

            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.KeyDown,
                Key = (int)e.Data.KeyCode
            });
        }

        private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;
                if (e.IsEventSimulated)
                {
                    return;
                }
            }
            else
            {
                SuppressIfNeeded(e);
            }

            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.KeyUp,
                Key = (int)e.Data.KeyCode
            });
        }

        private void OnMouseMoved(object? sender, MouseHookEventArgs e)
        {
            HandleMouseMove(e);
        }

        private void OnMouseDragged(object? sender, MouseHookEventArgs e)
        {
            HandleMouseMove(e);
        }

        private void OnMousePressed(object? sender, MouseHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;
                if (e.IsEventSimulated)
                {
                    return;
                }
            }
            else
            {
                SuppressIfNeeded(e);
            }

            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseDown,
                MouseButton = (int)e.Data.Button
            });
        }

        private void OnMouseReleased(object? sender, MouseHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;
                if (e.IsEventSimulated)
                {
                    return;
                }
            }
            else
            {
                SuppressIfNeeded(e);
            }

            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseUp,
                MouseButton = (int)e.Data.Button
            });
        }

        private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;
                if (e.IsEventSimulated)
                {
                    return;
                }
            }
            else
            {
                SuppressIfNeeded(e);
            }

            var delta = _suppressLocalInput
                ? ApplyScrollScale(e.Data.Rotation)
                : e.Data.Rotation;

            if (delta == 0)
            {
                return;
            }

            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseWheel,
                MouseButton = delta
            });
        }

        private void RaiseInputReceived(InputEvent inputEvent)
        {
            InputReceived?.Invoke(this, inputEvent);
        }

        private void SuppressIfNeeded(HookEventArgs e)
        {
            if (!_suppressLocalInput || e.IsEventSimulated)
            {
                return;
            }

            e.SuppressEvent = true;
        }

        private void HandleMouseMove(MouseHookEventArgs e)
        {
            if (_suppressLocalInput)
            {
                e.SuppressEvent = true;

                if (_isResettingPosition && e.IsEventSimulated)
                {
                    _isResettingPosition = false;
                    return;
                }

                if (e.IsEventSimulated)
                {
                    return;
                }

                if (_isResettingPosition)
                {
                    _isResettingPosition = false;
                }

                EnsureCenterInitialized(e.Data.X, e.Data.Y);

                var deltaX = e.Data.X - _lastX;
                var deltaY = e.Data.Y - _lastY;
                if (deltaX == 0 && deltaY == 0)
                {
                    return;
                }

                RaiseInputReceived(new InputEvent
                {
                    EventType = InputEventType.MouseMove,
                    MouseX = deltaX,
                    MouseY = deltaY,
                    IsRelative = true
                });

                LockPointerToCenter();
                return;
            }

            SuppressIfNeeded(e);
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseMove,
                MouseX = e.Data.X,
                MouseY = e.Data.Y
            });
        }

        private void ResetCaptureState()
        {
            _centerInitialized = false;
            _isResettingPosition = false;
            _centerX = 0;
            _centerY = 0;
            _lastX = 0;
            _lastY = 0;
            _scrollRemainder = 0;
        }

        private void InitializeCenterFromBounds()
        {
            if (_boundsWidth is not { } width || _boundsHeight is not { } height)
            {
                return;
            }

            if (width <= 0 || height <= 0)
            {
                return;
            }

            _centerX = width / 2;
            _centerY = height / 2;
            _lastX = _centerX;
            _lastY = _centerY;
            _centerInitialized = true;
        }

        private void EnsureCenterInitialized(int fallbackX, int fallbackY)
        {
            if (_centerInitialized)
            {
                return;
            }

            InitializeCenterFromBounds();
            if (_centerInitialized)
            {
                return;
            }

            _centerX = fallbackX;
            _centerY = fallbackY;
            _lastX = _centerX;
            _lastY = _centerY;
            _centerInitialized = true;
        }

        private void LockPointerToCenter()
        {
            if (!_centerInitialized)
            {
                return;
            }

            _lastX = _centerX;
            _lastY = _centerY;
            _isResettingPosition = true;
            try
            {
                _eventSimulator.SimulateMouseMovement((short)_centerX, (short)_centerY);
            }
            catch (Exception ex)
            {
                _isResettingPosition = false;
                Log.Warning(ex, "Failed to lock mouse position.");
            }
        }

        private int ApplyScrollScale(int delta)
        {
            if (delta == 0)
            {
                return 0;
            }

            var scaled = (delta * ScrollScale) + _scrollRemainder;
            var output = (int)Math.Truncate(scaled);
            _scrollRemainder = scaled - output;
            return output;
        }
    }
}
