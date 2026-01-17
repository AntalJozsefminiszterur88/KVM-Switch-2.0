namespace KvmSwitch.Core.Models
{
    public enum ButtonMappingTarget
    {
        Local,
        InputProvider,
        Remote
    }

    public sealed class ButtonMapping
    {
        public string ButtonId { get; set; } = string.Empty;
        public ButtonMappingTarget Target { get; set; } = ButtonMappingTarget.Local;
        public string Action { get; set; } = string.Empty;
    }
}
