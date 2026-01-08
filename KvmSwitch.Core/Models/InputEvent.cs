namespace KvmSwitch.Core.Models
{
    public enum InputEventType
    {
        KeyDown,
        KeyUp,
        MouseMove,
        MouseDown,
        MouseUp,
        MouseWheel
    }

    public sealed record InputEvent
    {
        public InputEventType EventType { get; init; }
        public int Key { get; init; }
        public int MouseX { get; init; }
        public int MouseY { get; init; }
        public int MouseButton { get; init; }
    }
}
