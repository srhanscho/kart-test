using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>Finds the PC's LAN IPv4 addresses, best candidate first.</summary>
public static class LanAddress
{
    static readonly string[] VirtualHints =
    {
        "virtual", "vmware", "vbox", "hyper-v", "vethernet", "wsl", "docker", "loopback",
        "bluetooth", "tap-", "tun", "vpn", "zerotier", "tailscale", "hamachi", "npcap"
    };

    public static List<string> GetCandidates()
    {
        var scored = new List<(string ip, int score)>();
        string routeIp = PrimaryRouteAddress();

        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                bool hasGateway = false;
                try
                {
                    hasGateway = props.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                }
                catch { /* not supported on some platforms */ }

                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool isVirtual = VirtualHints.Any(desc.Contains);

                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    int score = 0;
                    if (IsPrivate(ua.Address)) score += 10;
                    if (hasGateway) score += 5;
                    if (!isVirtual) score += 5;
                    if (ip == routeIp) score += 8;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 2;
                    scored.Add((ip, score));
                }
            }
        }
        catch { /* fall back to the route address below */ }

        var result = scored.OrderByDescending(s => s.score).Select(s => s.ip).Distinct().ToList();
        if (result.Count == 0 && routeIp != null) result.Add(routeIp);
        if (result.Count == 0) result.Add("127.0.0.1");
        return result;
    }

    /// <summary>Local address the OS would use for internet traffic (UDP connect sends no packets).</summary>
    static string PrimaryRouteAddress()
    {
        try
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
        catch
        {
            return null;
        }
    }

    static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
