using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Pome.Infrastructure;

/// <summary>Restores a verified UDIF/GPT recovery image into a new fixed VHD.
/// No disk is mounted. Hyper-V converts this intermediate to VHDX separately.</summary>
public static class RecoveryDisk
{
    private const long MaximumSize = 64L * 1024 * 1024 * 1024;
    private const int MaximumChunk = 16 * 1024 * 1024;
    private sealed record Chunk(long Offset, int Kind, long Bytes, long SourceOffset, int CompressedBytes);
    private sealed record Image(long Size, Chunk[] Chunks);

    public static long GetVirtualSize(string path)
    {
        using var input = File.OpenRead(path);
        return ReadImage(input).Size;
    }

    public static async Task RestoreAsync(string image, string destination, CancellationToken ct = default)
    {
        await using var input = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        var layout = ReadImage(input);
        if (File.Exists(destination)) throw new IOException("The recovery destination already exists.");
        var partial = destination + ".partial";
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            output.SetLength(checked(layout.Size + 512));
            foreach (var chunk in layout.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                if (chunk.Kind is 0 or 2) continue; // newly extended file is zero-filled
                var compressed = new byte[chunk.CompressedBytes];
                input.Position = chunk.SourceOffset;
                await input.ReadExactlyAsync(compressed, ct);
                byte[] decoded;
                if (chunk.Kind == 1) decoded = compressed;
                else
                {
                    decoded = new byte[checked((int)chunk.Bytes)];
                    using var source = new MemoryStream(compressed, false);
                    using var zlib = new ZLibStream(source, CompressionMode.Decompress);
                    await zlib.ReadExactlyAsync(decoded, ct);
                    if (zlib.ReadByte() != -1) throw new InvalidDataException("Recovery chunk exceeds its declared size.");
                }
                if (decoded.LongLength != chunk.Bytes) throw new InvalidDataException("Recovery chunk size mismatch.");
                output.Position = chunk.Offset;
                await output.WriteAsync(decoded, ct);
            }
            ValidateGpt(output, layout.Size);
            output.Position = layout.Size;
            await output.WriteAsync(CreateFooter(layout.Size), ct);
            await output.FlushAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(partial, destination); // never replace an existing disk
    }

    private static Image ReadImage(FileStream input)
    {
        if (input.Length < 512) throw new InvalidDataException("Recovery image is too short.");
        var trailer = new byte[512]; input.Position = input.Length - 512; input.ReadExactly(trailer);
        if (!trailer.AsSpan(0, 4).SequenceEqual("koly"u8)) throw new InvalidDataException("Expected a UDIF recovery image.");
        long Number(byte[] data, int offset) => checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8)));
        var size = checked(Number(trailer, 492) * 512);
        var xmlOffset = Number(trailer, 216); var xmlLength = Number(trailer, 224); var dataFork = Number(trailer, 24);
        if (size is < 1024 * 1024 or > MaximumSize || xmlLength is <= 0 or > 16 * 1024 * 1024 || xmlOffset < 0 || xmlOffset > input.Length - xmlLength)
            throw new InvalidDataException("Unsupported recovery image bounds.");
        var xml = new byte[(int)xmlLength]; input.Position = xmlOffset; input.ReadExactly(xml);
        using var xmlStream = new MemoryStream(xml, false);
        using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        XElement Value(XElement dict, string key)
        {
            var keys = dict.Elements("key").Where(x => x.Value == key).ToArray();
            if (keys.Length != 1 || keys[0].ElementsAfterSelf().FirstOrDefault() is not { } value || value.Name == "key") throw new InvalidDataException("Invalid recovery block map.");
            return value;
        }
        var root = document.Root?.Element("dict") ?? throw new InvalidDataException("Invalid recovery property list.");
        var blocks = Value(Value(root, "resource-fork"), "blkx");
        if (blocks.Name != "array" || blocks.Elements().Count() > 128) throw new InvalidDataException("Unsupported recovery partition map.");
        var chunks = new List<Chunk>();
        foreach (var block in blocks.Elements("dict"))
        {
            var encoded = Value(block, "Data");
            if (encoded.Name != "data") throw new InvalidDataException("Invalid recovery block data.");
            var table = Convert.FromBase64String(encoded.Value);
            if (table.Length < 204 || !table.AsSpan(0, 4).SequenceEqual("mish"u8)) throw new InvalidDataException("Invalid recovery block header.");
            var first = Number(table, 8); var sectors = Number(table, 16); var sourceBase = checked(dataFork + Number(table, 24));
            var count = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(200));
            if (first < 0 || sectors < 0 || first > size / 512 - sectors || count > 100_000 || 204L + 40L * count > table.Length)
                throw new InvalidDataException("Recovery partition exceeds image bounds.");
            for (var n = 0; n < count; n++)
            {
                var offset = checked(204 + 40 * (int)n);
                var kind = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset));
                var start = Number(table, offset + 8); var length = Number(table, offset + 16);
                if (length == 0 || kind == uint.MaxValue) continue;
                var sourceOffset = checked(sourceBase + Number(table, offset + 24)); var compressed = Number(table, offset + 32);
                var bytes = checked(length * 512);
                if (kind is not (0 or 1 or 2 or 0x80000005) || start > sectors - length || start < 0 || length < 0 ||
                    (kind is not (0 or 2) && (bytes > MaximumChunk || compressed is <= 0 or > MaximumChunk + 1024 * 1024 || sourceOffset > input.Length - compressed)))
                    throw new InvalidDataException("Unsupported recovery compression or chunk bounds.");
                chunks.Add(new(checked((first + start) * 512), unchecked((int)kind), bytes, sourceOffset, checked((int)compressed)));
                if (chunks.Count > 100_000) throw new InvalidDataException("Too many recovery chunks.");
            }
        }
        var sorted = chunks.OrderBy(x => x.Offset).ToArray(); long end = 0;
        foreach (var chunk in sorted)
        {
            if (chunk.Offset != end) throw new InvalidDataException("Recovery sectors overlap or leave a gap.");
            end = checked(end + chunk.Bytes);
        }
        if (end != size) throw new InvalidDataException("Incomplete recovery disk image.");
        return new(size, sorted);
    }

    private static void ValidateGpt(Stream disk, long size)
    {
        var probe = new byte[512]; disk.Position = 512; disk.ReadExactly(probe);
        // OS X 10.7–10.10 recoveries use an Apple Partition Map; OpenCore reads them through OpenPartitionDxe.
        if (!probe.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) { ValidateApplePartitionMap(disk, size); return; }
        foreach (var lba in new[] { 1L, size / 512 - 1 })
        {
            var header = new byte[512]; disk.Position = lba * 512; disk.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) throw new InvalidDataException("Recovery must contain a GPT disk image.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            header.AsSpan(16, 4).Clear();
            if (length is < 92 or > 512 || Crc32(header.AsSpan(0, (int)length)) != expected || BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)) != (ulong)lba)
                throw new InvalidDataException("Recovery GPT header checksum mismatch.");
            var entries = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
            var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            var bytes = (ulong)count * entrySize;
            if (bytes is 0 or > 1024 * 1024 || entries >= (ulong)size / 512 || bytes > (ulong)size - entries * 512)
                throw new InvalidDataException("Invalid recovery GPT array bounds.");
            var array = new byte[(int)bytes]; disk.Position = checked((long)entries * 512); disk.ReadExactly(array);
            if (Crc32(array) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88))) throw new InvalidDataException("Recovery GPT array checksum mismatch.");
        }
    }

    /// <summary>
    /// Validates a Driver Descriptor Map plus Apple Partition Map: every map entry carries the "PM"
    /// signature and the same entry count, lies inside the disk, and exactly one Apple_HFS partition exists.
    /// </summary>
    private static void ValidateApplePartitionMap(Stream disk, long size)
    {
        var ddm = new byte[512]; disk.Position = 0; disk.ReadExactly(ddm);
        // Apple's 10.7-era recoveries use 2048-byte APM blocks (CD style); offsets are in that unit.
        var block = BinaryPrimitives.ReadUInt16BigEndian(ddm.AsSpan(2));
        if (ddm[0] != (byte)'E' || ddm[1] != (byte)'R' || block is not (512 or 2048))
            throw new InvalidDataException("Recovery must contain a GPT or Apple Partition Map disk image.");
        var blocks = size / block;
        var entry = new byte[512]; disk.Position = block; disk.ReadExactly(entry);
        var count = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4));
        if (count is 0 or > 63 || count >= blocks) throw new InvalidDataException("Invalid recovery partition map size.");
        var hfs = 0;
        for (var index = 1L; index <= count; index++)
        {
            disk.Position = index * block; disk.ReadExactly(entry);
            var start = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(12));
            if (entry[0] != (byte)'P' || entry[1] != (byte)'M' || BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4)) != count
                || length == 0 || (ulong)start + length > (ulong)blocks)
                throw new InvalidDataException("Recovery Apple Partition Map entry is invalid.");
            var type = System.Text.Encoding.ASCII.GetString(entry, 48, 32).TrimEnd('\0');
            if (type == "Apple_HFS")
            {
                hfs++;
                var volume = new byte[512]; disk.Position = (long)start * block + 1024; disk.ReadExactly(volume);
                if (volume[0] != (byte)'H' || volume[1] is not ((byte)'+' or (byte)'X'))
                    throw new InvalidDataException("The recovery Apple_HFS partition does not contain an HFS+ volume.");
            }
        }
        if (hfs != 1) throw new InvalidDataException("Recovery must contain exactly one Apple_HFS partition.");
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u); }
        return ~crc;
    }

    private static byte[] CreateFooter(long size)
    {
        var data = new byte[512]; "conectix"u8.CopyTo(data);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset), value);
        U32(8, 2); U32(12, 0x10000); U64(16, ulong.MaxValue);
        U32(24, checked((uint)(DateTimeOffset.UtcNow - new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds));
        "pome"u8.CopyTo(data.AsSpan(28)); U32(32, 0x10000); "Wi2k"u8.CopyTo(data.AsSpan(36));
        U64(40, (ulong)size); U64(48, (ulong)size);
        var sectors = Math.Min(size / 512, 65535L * 16 * 255); long spt, heads, tracks;
        if (sectors >= 65535L * 16 * 63) { spt = 255; heads = 16; tracks = sectors / spt; }
        else
        {
            spt = 17; tracks = sectors / spt; heads = Math.Max(4, (tracks + 1023) / 1024);
            if (tracks >= heads * 1024 || heads > 16) { spt = 31; heads = 16; tracks = sectors / spt; }
            if (tracks >= heads * 1024) { spt = 63; heads = 16; tracks = sectors / spt; }
        }
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(56), (ushort)(tracks / heads)); data[58] = (byte)heads; data[59] = (byte)spt;
        U32(60, 2); Guid.NewGuid().ToByteArray(true).CopyTo(data, 68);
        U32(64, ~data.Aggregate(0u, (sum, value) => sum + value));
        return data;
    }
}
