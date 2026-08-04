// Read an sbp slot table without its banks
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Lists the slots in a .sbp (sound-bank package) without reading a bank.
/// The friendliest layout of the lot: 'SBPL' | fileCount u8 | headerSize u16 |
/// pad, then fileCount x [ magic4 | offset u32 | size i32 ], then 16-aligned
/// blobs. The header states its own index size, so one prefix read of exactly
/// headerSize bytes covers everything — no sizing pass, no widening.
///
/// Slots are positional, not named: entry i is "i.&lt;magic&gt;" (0.bnk, 1.stp …).
/// </summary>
public static class SbpIndex
{
    private const uint MagicSbpl = 0x4C504253u;   // "SBPL"
    private const int FileHeader = 8, SlotSize = 12;

    /// <summary>One slot: its 4-char type tag and where its blob lives.</summary>
    public sealed record Slot(int Index, string Magic, uint Offset, int Size)
    {
        /// <summary>The name the tools address this slot by.</summary>
        public string Name => $"{Index}.{Magic}";
    }

    public static List<Slot> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        long stored = totalSize;
        if (stored < FileHeader) return null;

        var head = read(0, FileHeader);
        if (head is null || head.Length < FileHeader || BitConverter.ToUInt32(head, 0) != MagicSbpl) return null;

        int count = head[4];
        int headerSize = BitConverter.ToUInt16(head, 5);
        if (headerSize < FileHeader + count * SlotSize || headerSize > stored) return null;

        var table = read(0, headerSize);
        if (table is null || table.Length < headerSize) return null;
        bytesRead = table.Length;

        var slots = new List<Slot>(count);
        for (int i = 0; i < count; i++)
        {
            int at = FileHeader + i * SlotSize;
            var magic = System.Text.Encoding.ASCII.GetString(table, at, 4).TrimEnd('\0');
            slots.Add(new Slot(i, magic,
                BitConverter.ToUInt32(table, at + 4),
                BitConverter.ToInt32(table, at + 8)));
        }
        return slots;
    }
}
