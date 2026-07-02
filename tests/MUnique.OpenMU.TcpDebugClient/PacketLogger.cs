namespace MUnique.OpenMU.TcpDebugClient;

using System.Buffers;

/// <summary>
/// Utility for logging MU network packets in a human-readable format.
/// </summary>
internal static class PacketLogger
{
    /// <summary>
    /// Logs a packet to the console.
    /// </summary>
    /// <param name="packet">The packet data.</param>
    /// <param name="hexDump">If true, prints the full hex dump.</param>
    internal static void LogPacket(ReadOnlySequence<byte> packet, bool hexDump)
    {
        if (packet.FirstSpan.Length < 3)
        {
            return;
        }

        var header = packet.FirstSpan[0];
        var length = packet.FirstSpan[1];
        var code = packet.FirstSpan[2];
        var subCode = packet.FirstSpan.Length > 3 ? packet.FirstSpan[3] : (byte)0;

        var typeName = header switch
        {
            0xC1 => "C1",
            0xC2 => "C2",
            0xC3 => "C3",
            0xC4 => "C4",
            _ => $"?{header:X2}",
        };

        var description = DescribePacket(code, subCode, length);

        Console.WriteLine(
            "[PKT] {0} len={1} code=0x{2:X2} sub=0x{3:X2}  {4}",
            typeName, length, code, subCode, description);

        if (hexDump && packet.FirstSpan.Length > 0)
        {
            var hex = BitConverter.ToString(packet.FirstSpan.ToArray());
            Console.WriteLine($"      {hex}");
        }
    }

    /// <summary>
    /// Formats a packet as a hex string.
    /// </summary>
    internal static string FormatHex(ReadOnlySequence<byte> packet)
    {
        if (packet.FirstSpan.Length == 0)
        {
            return "(empty)";
        }

        return BitConverter.ToString(packet.FirstSpan.ToArray());
    }

    private static string DescribePacket(byte code, byte subCode, int length)
    {
        return (code, subCode) switch
        {
            (0x00, _) => "Chat message",
            (0x0E, _) => "Ping",
            (0x11, _) => "Hit",
            (0xD4, _) => "Walk",
            (0xF1, 0x01) => "Login response",
            (0xF1, 0x02) => "Logout",
            (0xF1, 0x03) => "Client ready / map change",
            (0xF3, 0x00) => "Character list",
            (0xF3, 0x01) => "Character info / inventory",
            (0xF3, 0x03) => "Character select / map enter",
            (0xF3, 0x05) => "Character stats / level update",
            (0xF3, 0x15) => "Character focus",
            (0xA6, _) => "Map data / coordinates",
            (0xA9, _) => "Object move / teleport",
            _ => $"Unknown (code=0x{code:X2}, sub=0x{subCode:X2})",
        };
    }
}
