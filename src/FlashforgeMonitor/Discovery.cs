using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FlashforgeMonitor;

public sealed record DiscoveredPrinter(string Ip, string Name, string Model, string? Serial, int CommandPort, string Status);

/// <summary>
/// Finds FlashForge printers on the local subnet via UDP broadcast/multicast (the same
/// probe FlashPrint/Orca use). Discovery does not cross VLANs/routers; enter the IP
/// manually in that case. Packet format from the MIT-licensed flashforge-python-api.
/// </summary>
public static class Discovery
{
    private static readonly int[] Ports = [48899, 19000, 8899];

    public static async Task<List<DiscoveredPrinter>> FindAsync(TimeSpan duration, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var targets = new List<IPEndPoint>();
        foreach (var port in Ports) targets.Add(new IPEndPoint(IPAddress.Broadcast, port));
        foreach (var b in SubnetBroadcasts()) targets.Add(new IPEndPoint(b, 48899));
        targets.Add(new IPEndPoint(IPAddress.Parse("225.0.0.9"), 8899));
        targets.Add(new IPEndPoint(IPAddress.Parse("225.0.0.9"), 19000));

        foreach (var t in targets)
        {
            try { await udp.SendAsync(Array.Empty<byte>(), t, ct); } catch (SocketException) { }
        }

        var found = new Dictionary<string, DiscoveredPrinter>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                if (Parse(r.Buffer, r.RemoteEndPoint.Address.ToString()) is { } p) found[p.Ip] = p;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return found.Values.OrderBy(p => p.Ip).ToList();
    }

    private static DiscoveredPrinter? Parse(byte[] b, string ip)
    {
        static int U16(byte[] b, int at) => (b[at] << 8) | b[at + 1];
        static string Str(byte[] b, int at, int len) =>
            Encoding.UTF8.GetString(b, at, Math.Min(len, b.Length - at)).Split('\0')[0].Trim();
        static string StatusName(int s) => s switch { 0 => "ready", 1 => "busy", 2 => "error", _ => "unknown" };

        if (b.Length >= 276) // modern (5M, 5M Pro, AD5X, Creator 5)
        {
            var pid = U16(b, 0x88);
            var model = pid switch
            {
                0x0023 => "Adventurer 5M",
                0x0024 => "Adventurer 5M Pro",
                0x0026 => "AD5X",
                0x0028 => "Creator 5",
                0x0029 => "Creator 5 Pro",
                _ => "FlashForge",
            };
            return new DiscoveredPrinter(ip, Str(b, 0, 0x84), model, Str(b, 0x92, 128), U16(b, 0x84), StatusName(U16(b, 0x8A)));
        }
        if (b.Length >= 140) // legacy (Adventurer 3/4)
            return new DiscoveredPrinter(ip, Str(b, 0, 0x80), "FlashForge (legacy)", null, U16(b, 0x84), StatusName(U16(b, 0x8A)));
        return null;
    }

    private static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }

    /// <summary>Checks which printer ports answer at an address (no login, safe during a print).</summary>
    public static async Task<object> TestAsync(string host, int tcpPort, int httpPort, int cameraPort, CancellationToken ct)
    {
        async Task<bool> Open(int port)
        {
            using var c = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try { await c.ConnectAsync(host, port, cts.Token); return true; }
            catch { return false; }
        }
        var control = Open(tcpPort);
        var http = Open(httpPort);
        var camera = Open(cameraPort);
        await Task.WhenAll(control, http, camera);
        var ok = control.Result;
        return new
        {
            ok,
            control = control.Result,
            http = http.Result,
            camera = camera.Result,
            message = ok
                ? $"Printer found at {host}" + (camera.Result ? " (camera available)" : "")
                : $"Nothing answering on {host}:{tcpPort}. Check the IP and that this machine can reach the printer.",
        };
    }
}
