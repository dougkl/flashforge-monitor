namespace FlashforgeMonitor;

public sealed class PrinterOptions
{
    /// <summary>Printer IP address or hostname. Required.</summary>
    public string Host { get; set; } = "";
    public int TcpPort { get; set; } = 8899;
    public int HttpPort { get; set; } = 8898;
    public int CameraPort { get; set; } = 8080;
    /// <summary>Optional. Detected automatically from the printer when empty.</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>Optional. Only needed for LAN-mode HTTP features (camera on/off, clear platform).</summary>
    public string CheckCode { get; set; } = "";
    public int PollSeconds { get; set; } = 3;
}

public sealed class MonitorOptions
{
    public string DataDirectory { get; set; } = "data";
    public string Username { get; set; } = "admin";
    /// <summary>When set, the whole app is protected with HTTP Basic auth.</summary>
    public string Password { get; set; } = "";
}

public sealed class NotificationOptions
{
    public List<WebhookOptions> Webhooks { get; set; } = [];
}

public sealed class WebhookOptions
{
    public string Url { get; set; } = "";
    /// <summary>json (default), ntfy, or discord.</summary>
    public string Type { get; set; } = "json";
    /// <summary>Events to send. Empty = all: started, finished, cancelled, paused, resumed, offline, online.</summary>
    public List<string> Events { get; set; } = [];
}
