using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuestApps;

// One physical headset, however it is reachable right now (cable, Wi-Fi, or only remembered).
public sealed class Headset
{
    public string Id { get; init; } = "";          // ro.serialno (= USB serial on Quest)
    public string Model { get; set; } = "?";
    public string? Name { get; set; }              // user-given name
    public string? Ip { get; set; }
    public string? UsbSerial { get; set; }         // connected by cable
    public string? WifiSerial { get; set; }        // connected over Wi-Fi ("ip:5555")
    public bool WifiReachable { get; set; }        // not connected, but adb answers on ip:5555
    public string? Problem { get; set; }           // "unauthorized" / "offline"

    // USB is preferred while plugged in; Wi-Fi takes over when the cable goes.
    public string? Serial => UsbSerial ?? WifiSerial;
    public bool IsConnected => Serial != null;

    public string DisplayText
    {
        get
        {
            var name = Label;
            if (Problem != null)
                return $"{name} — {Problem}";
            if (UsbSerial != null && WifiSerial != null)
                return $"{name} — USB + Wi-Fi";
            if (UsbSerial != null)
                return $"{name} — USB";
            if (WifiSerial != null)
                return $"{name} — Wi-Fi {Ip}";
            return WifiReachable ? $"{name} — nearby ({Ip})" : $"{name} — not found" + (Ip != null ? $" ({Ip})" : "");
        }
    }

    // "Station 3" or "Quest 3 •0402" — tells apart several headsets of the same model
    public string Label => Name ?? $"{Model.Replace('_', ' ')} •{(Id.Length > 4 ? Id[^4..] : Id)}";

    public override string ToString() => DisplayText;
}

internal static class DeviceDiscovery
{
    // Everything adb sees, merged per headset with the remembered ones.
    // probeRemembered: check (quickly) whether remembered headsets answer on Wi-Fi.
    public static List<Headset> Discover(AdbService adb, bool probeRemembered)
    {
        var byId = new Dictionary<string, Headset>(StringComparer.Ordinal);
        foreach (var d in adb.ListDevices())
        {
            bool wifi = AdbService.IsWifiSerial(d.Serial);
            if (d.State != "device")
            {
                // e.g. "unauthorized" until the USB debugging prompt is accepted in the headset
                if (!wifi)
                    byId[d.Serial] = new Headset { Id = d.Serial, Model = "Headset " + d.Serial, Problem = d.State, UsbSerial = null };
                continue;
            }

            string id = d.Serial;
            string? ip = null;
            if (wifi)
            {
                ip = d.Serial[..d.Serial.LastIndexOf(':')];
                try
                {
                    var serialNo = adb.RunOn(d.Serial, "shell getprop ro.serialno").Trim();
                    if (serialNo.Length > 0 && !serialNo.Contains(' '))
                        id = serialNo;
                }
                catch { }
                // serial number unreadable (connection still settling): match by remembered address
                if (id == d.Serial)
                    id = HeadsetMemory.Load().FirstOrDefault(k => k.Ip == ip)?.Id ?? id;
            }
            else
            {
                try { ip = adb.GetWifiIp(d.Serial); } catch { }
            }

            if (!byId.TryGetValue(id, out var h))
                byId[id] = h = new Headset { Id = id };
            h.Model = d.Model;
            h.Ip = ip ?? h.Ip;
            if (wifi)
                h.WifiSerial = d.Serial;
            else
                h.UsbSerial = d.Serial;
            if (!AdbService.IsWifiSerial(id)) // never remember an address as if it were a headset
                HeadsetMemory.Remember(id, d.Model, ip);
        }

        foreach (var known in HeadsetMemory.Load())
        {
            if (byId.TryGetValue(known.Id, out var connected))
            {
                connected.Name = known.Name;
                continue;
            }
            byId[known.Id] = new Headset { Id = known.Id, Model = known.Model, Name = known.Name, Ip = known.Ip };
        }

        var all = byId.Values.ToList();
        if (probeRemembered)
        {
            Parallel.ForEach(all.Where(h => !h.IsConnected && h.Ip != null && h.Problem == null),
                h => h.WifiReachable = AdbService.CanReach(h.Ip!));
        }

        return all
            .OrderByDescending(h => h.IsConnected)
            .ThenByDescending(h => h.WifiReachable)
            .ThenBy(h => h.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Current adb serial of a headset: cable first, then Wi-Fi (reconnecting when it answers).
    public static string? ResolveSerial(AdbService adb, string id, string? ip)
    {
        var ready = adb.ListDevices().Where(d => d.State == "device").Select(d => d.Serial).ToList();
        if (ready.Contains(id))
            return id;
        ip ??= HeadsetMemory.Load().FirstOrDefault(h => h.Id == id)?.Ip;
        if (ip == null)
            return null;
        var wifi = $"{ip}:{AdbService.WifiPort}";
        if (ready.Contains(wifi))
            return wifi;
        return AdbService.CanReach(ip) && adb.Connect(ip) ? wifi : null;
    }

    // Look for adb listening on port 5555 anywhere in the PC's local networks
    // (finds headsets whose Wi-Fi ADB is on but whose address changed or is unknown).
    public static List<string> ScanNetwork(Action<int, int>? progress = null)
    {
        var hosts = LocalHosts().ToList();
        var found = new List<string>();
        int done = 0;
        using var gate = new SemaphoreSlim(96);
        var tasks = hosts.Select(async ip =>
        {
            await gate.WaitAsync();
            try
            {
                if (await IsPortOpenAsync(ip, AdbService.WifiPort, 400))
                    lock (found) found.Add(ip.ToString());
            }
            finally
            {
                gate.Release();
                var d = Interlocked.Increment(ref done);
                if (d % 16 == 0 || d == hosts.Count)
                    progress?.Invoke(d, hosts.Count);
            }
        }).ToArray();
        Task.WaitAll(tasks);
        return found;
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, int timeoutMs)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(ip, port);
            return await Task.WhenAny(connect, Task.Delay(timeoutMs)) == connect && client.Connected;
        }
        catch { return false; }
    }

    // Every address in the PC's real (non-virtual) IPv4 networks, at most /22 (1022 hosts) each.
    private static IEnumerable<IPAddress> LocalHosts()
    {
        var seen = new HashSet<uint>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet) ||
                nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength < 22)
                    continue;
                var bytes = ua.Address.GetAddressBytes();
                uint self = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                uint mask = ua.PrefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> ua.PrefixLength);
                uint net = self & mask;
                uint broadcast = net | ~mask;
                for (uint a = net + 1; a < broadcast; a++)
                {
                    if (a == self || !seen.Add(a))
                        continue;
                    yield return new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });
                }
            }
        }
    }
}
