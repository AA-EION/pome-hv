using System.Buffers.Binary;
using System.Text;
using Pome.Core;

namespace Pome.Infrastructure;

public static class PipeProtocol
{
    public const int MaximumMessageBytes = 64 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Write(value));
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("IPC message exceeds the limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid IPC frame length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return Json.Read<T>(Encoding.UTF8.GetString(bytes));
    }
}
