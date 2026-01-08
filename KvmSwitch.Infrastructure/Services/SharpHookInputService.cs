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
        private readonly object _sync = new();
        private readonly EventSimulator _eventSimulator = new();
        private TaskPoolGlobalHook? _hook;
        private Task? _hookTask;

        public event EventHandler<InputEvent>? InputReceived;

        public void Start()
        {
            lock (_sync)
            {
                if (_hook != null)
                {
                    return;
                }

                try
                {
                    _hook = new TaskPoolGlobalHook();
                    _hook.KeyPressed += OnKeyPressed;
                    _hook.KeyReleased += OnKeyReleased;
                    _hook.MouseMoved += OnMouseMoved;
                    _hook.MousePressed += OnMousePressed;
                    _hook.MouseReleased += OnMouseReleased;
                    _hook.MouseWheel += OnMouseWheel;

                    _hookTask = _hook.RunAsync();
                    _hookTask.ContinueWith(
                        t => Log.Error(t.Exception, "SharpHook input hook terminated with an error."),
                        TaskContinuationOptions.OnlyOnFaulted);

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
                if (_hook == null)
                {
                    return;
                }

                try
                {
                    _hook.Dispose();
                    _hook = null;
                    _hookTask = null;
                    Log.Information("SharpHook input service stopped.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to stop SharpHook input service.");
                    throw;
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
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.KeyDown,
                Key = (int)e.Data.KeyCode
            });
        }

        private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.KeyUp,
                Key = (int)e.Data.KeyCode
            });
        }

        private void OnMouseMoved(object? sender, MouseHookEventArgs e)
        {
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseMove,
                MouseX = e.Data.X,
                MouseY = e.Data.Y
            });
        }

        private void OnMousePressed(object? sender, MouseHookEventArgs e)
        {
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseDown,
                MouseX = e.Data.X,
                MouseY = e.Data.Y,
                MouseButton = (int)e.Data.Button
            });
        }

        private void OnMouseReleased(object? sender, MouseHookEventArgs e)
        {
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseUp,
                MouseX = e.Data.X,
                MouseY = e.Data.Y,
                MouseButton = (int)e.Data.Button
            });
        }

        private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
        {
            RaiseInputReceived(new InputEvent
            {
                EventType = InputEventType.MouseWheel,
                MouseX = e.Data.X,
                MouseY = e.Data.Y,
                MouseButton = e.Data.Rotation
            });
        }

        private void RaiseInputReceived(InputEvent inputEvent)
        {
            InputReceived?.Invoke(this, inputEvent);
        }
    }
}
