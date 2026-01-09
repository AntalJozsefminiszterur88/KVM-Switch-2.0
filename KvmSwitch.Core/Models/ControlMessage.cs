using MessagePack;

namespace KvmSwitch.Core.Models;

public enum ControlCommand
{
    StartStreaming,
    StopStreaming
}

[MessagePackObject]
public class ControlMessage
{
    [Key(0)]
    public ControlCommand Command { get; set; }
}
