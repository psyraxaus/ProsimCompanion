using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Sends a Wake-on-LAN "magic packet" so the self-hosted LLM box powers itself on
/// (Prosim2FO parity).
///
/// Prerequisites the app CANNOT satisfy (one-time, manual setup): WoL must already be
/// enabled in the target's BIOS/UEFI and NIC driver/power settings; the target must be on
/// wired Ethernet on the SAME subnet as this PC — magic-packet broadcasts do not cross
/// subnets (routers drop directed broadcasts by default), and WoL over WiFi is unreliable.
///
/// The send is fire-and-forget: WoL is connectionless UDP with no acknowledgement, so
/// readiness is confirmed by actually reaching the endpoint ("Test LLM connection" or the
/// first real completion), never by this call.
/// </summary>
public static class WakeOnLan
{
    /// <summary>Builds the 102-byte magic packet (6×0xFF followed by the 6-byte MAC repeated
    /// 16 times) and broadcasts it as a single UDP datagram. Never throws — failures are
    /// logged and swallowed. Returns a human-readable outcome for the UI.</summary>
    public static string Send(string? macAddress, string? broadcastAddress, int port, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            if (!TryParseMac(macAddress, out var mac))
            {
                logger.LogWarning("Wake-on-LAN skipped: invalid MAC address '{Mac}'", macAddress);
                return $"Invalid MAC address \"{macAddress}\" — expected 6 hex pairs (':' or '-' separators).";
            }

            var broadcast = string.IsNullOrWhiteSpace(broadcastAddress)
                ? "255.255.255.255"
                : broadcastAddress.Trim();
            if (!IPAddress.TryParse(broadcast, out var target))
            {
                logger.LogWarning("Wake-on-LAN skipped: invalid broadcast address '{Address}'", broadcastAddress);
                return $"Invalid broadcast address \"{broadcastAddress}\".";
            }

            if (port is <= 0 or > 65535)
            {
                port = 9; // conventional WoL discard port
            }

            // 6 bytes of 0xFF, then the MAC repeated 16 times → 102 bytes.
            var packet = new byte[6 + 16 * 6];
            for (var i = 0; i < 6; i++)
            {
                packet[i] = 0xFF;
            }

            for (var repeat = 0; repeat < 16; repeat++)
            {
                Array.Copy(mac, 0, packet, 6 + repeat * 6, 6);
            }

            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Send(packet, packet.Length, new IPEndPoint(target, port));

            var formatted = FormatMac(mac);
            logger.LogInformation("Wake-on-LAN magic packet sent to {Mac} via {Broadcast}:{Port}",
                formatted, broadcast, port);
            return $"Magic packet sent to {formatted} via {broadcast}:{port} — give the host a minute, then Test LLM connection.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wake-on-LAN send failed (non-fatal)");
            return $"Wake-on-LAN send failed: {ex.Message}";
        }
    }

    /// <summary>Parses a MAC string (':'/'-'/space/no separators) into 6 bytes.</summary>
    public static bool TryParseMac(string? macAddress, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return false;
        }

        var hex = macAddress
            .Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();
        if (hex.Length != 12)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        mac = bytes;
        return true;
    }

    private static string FormatMac(byte[] mac)
        => string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
