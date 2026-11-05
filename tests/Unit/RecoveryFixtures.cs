using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

internal static class RecoveryFixtures
{
    public static byte[] Write(string path, bool badGpt = false, bool gap = false, uint compression = 0x80000005, int apmBlock = 0, bool badApm = false)
    {
        var disk = new byte[2 * 1024 * 1024];
        var sectors = disk.Length / 512;
        if (apmBlock > 0) { WriteApm(disk, apmBlock, badApm); return Encode(path, disk, sectors, gap, compression); }
        // A protective MBR, two GPT headers and matching partition arrays.
        disk[510] = 0x55; disk[511] = 0xaa;
        var entries = new byte[512];
        new Guid("48465300-0000-11aa-aa11-00306543ecac").ToByteArray().CopyTo(entries, 0);
        Guid.NewGuid().ToByteArray().CopyTo(entries, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(32), 40);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(40), (ulong)sectors - 3);
        Encoding.Unicode.GetBytes("Recovery fixture").CopyTo(entries, 56);
        entries.CopyTo(disk, 2 * 512); entries.CopyTo(disk, (sectors - 2) * 512);
        var guid = Guid.NewGuid().ToByteArray();
        foreach (var lba in new[] { 1, sectors - 1 })
        {
            var header = disk.AsSpan(lba * 512, 512);
            "EFI PART"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
            BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)lba);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)(lba == 1 ? sectors - 1 : 1));
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 34);
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)sectors - 3);
            guid.CopyTo(header[56..]);
            BinaryPrimitives.WriteUInt64LittleEndian(header[72..], (ulong)(lba == 1 ? 2 : sectors - 2));
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc(header[..92]));
        }
        "H+"u8.CopyTo(disk.AsSpan(40 * 512 + 1024));
        // Nonzero contents prove that decompression preserves disk sectors.
        Random.Shared.NextBytes(disk.AsSpan(64 * 512, 512));
        if (badGpt) disk[512 + 88] ^= 1;
        return Encode(path, disk, sectors, gap, compression);
    }
    // Driver Descriptor Map + Apple Partition Map, as used by OS X 10.7-era recoveries (2048-byte blocks).
    private static void WriteApm(byte[] disk, int block, bool bad)
    {
        var blocks = disk.Length / block;
        disk[0] = (byte)'E'; disk[1] = (byte)'R'; BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), (ushort)block);
        void Entry(int index, uint start, uint length, string type)
        {
            var entry = disk.AsSpan(index * block, 512);
            entry[0] = (byte)'P'; entry[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], 2);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], start);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], length);
            Encoding.ASCII.GetBytes(type).CopyTo(entry[48..]);
        }
        Entry(1, 1, 3, "Apple_partition_map");
        Entry(2, 4, (uint)(bad ? blocks : blocks - 4), "Apple_HFS");
        "H+"u8.CopyTo(disk.AsSpan(4 * block + 1024));
        Random.Shared.NextBytes(disk.AsSpan(8 * block, 512));
    }
    private static byte[] Encode(string path, byte[] disk, int sectors, bool gap, uint compression)
    {
        using var encoded = new MemoryStream();
        if (compression == 1) encoded.Write(disk);
        else { using var zlib = new ZLibStream(encoded, CompressionLevel.Fastest, true); zlib.Write(disk); }
        var data = encoded.ToArray();
        var table = new byte[244]; "mish"u8.CopyTo(table);
        BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(16), (ulong)sectors);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(200), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(204), compression);
        BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(212), gap ? 1UL : 0UL);
        BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(220), (ulong)sectors - (gap ? 1UL : 0UL));
        BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(236), (ulong)data.Length);
        var block = new XElement("dict", new XElement("key", "Data"), new XElement("data", Convert.ToBase64String(table)));
        var resources = new XElement("dict", new XElement("key", "blkx"), new XElement("array", block));
        var plist = new XElement("plist", new XElement("dict", new XElement("key", "resource-fork"), resources));
        var xml = Encoding.UTF8.GetBytes(new XDocument(plist).ToString());
        var trailer = new byte[512]; "koly"u8.CopyTo(trailer);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(216), (ulong)data.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(224), (ulong)xml.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(492), (ulong)sectors);
        using var file = File.Create(path); file.Write(data); file.Write(xml); file.Write(trailer);
        return disk;
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (var b in bytes) { value ^= b; for (var i = 0; i < 8; i++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1)); }
        return ~value;
    }
}
