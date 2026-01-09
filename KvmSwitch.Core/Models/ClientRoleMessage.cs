using MessagePack;

namespace KvmSwitch.Core.Models;

public enum ClientRole
{
    InputProvider,
    Receiver
}

[MessagePackObject]
public sealed class ClientRoleMessage
{
    [Key(0)]
    public ClientRole Role { get; set; }

    [Key(1)]
    public int? ScreenWidth { get; set; }

    [Key(2)]
    public int? ScreenHeight { get; set; }
}
