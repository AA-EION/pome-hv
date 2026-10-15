using System.IO.Compression;

namespace Pome.Infrastructure;

public static class SafeArchive
{
    public static void Extract(string archive, string destination)
    {
        if (Directory.Exists(destination)) throw new IOException("Extraction requires a new directory.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Archive contains too many entries.");
        var root = Path.GetFullPath(destination); long total = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            total = checked(total + entry.Length);
            if (!SafePaths.IsWithin(path, root) || path == root || entry.FullName.Contains(':') ||
                entry.FullName.Split('/', '\\').Any(x => x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || total > 512L * 1024 * 1024 || !targets.Add(path))
                throw new InvalidDataException("Unsafe archive entry or excessive expanded size.");
        }
        Directory.CreateDirectory(root);
        zip.ExtractToDirectory(root, false);
    }
}
