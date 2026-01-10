using MessagePack;

namespace KvmSwitch.Core.Models;

[MessagePackObject]
public sealed class ClipboardMessage
{
    [Key(0)]
    public string Text { get; set; } = string.Empty;
}
