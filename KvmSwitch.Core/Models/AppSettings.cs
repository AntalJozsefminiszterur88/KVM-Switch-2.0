namespace KvmSwitch.Core.Models
{
    public sealed class AppSettings
    {
        public int Port { get; set; } = 65432;
        public string HostMonitorCode { get; set; } = "17";
        public string ClientMonitorCode { get; set; } = "18";
        public bool AutoStartEnabled { get; set; }
        public string? ReceiverHostIp { get; set; } = "192.168.0.19";
        public int SelectedRole { get; set; } = 1;
        public bool AutoStartService { get; set; }
        public bool StartInTray { get; set; }
    }
}
