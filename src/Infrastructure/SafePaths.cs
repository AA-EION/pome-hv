using System.Security.Cryptography;

namespace Pome.Infrastructure;

public static class SafePaths
{
    public static string RequireLocal(string path, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith('\\') || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("Use an absolute local drive path without alternate data streams.");
        var full = Path.GetFullPath(path);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("The selected path does not exist.", full);
        var cursor = full;
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Symbolic links and directory junctions are not permitted for privileged operations.");
            cursor = Path.GetDirectoryName(cursor);
        }
        return full;
    }
    public static string NewOperationDirectory(string root, Guid id)
    {
        root = RequireLocal(root);
        if (!Directory.Exists(root) || id == Guid.Empty) throw new InvalidDataException("Choose an existing storage directory.");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (IsWithin(root, system) || IsWithin(root, programFiles)) throw new InvalidDataException("Choose a data directory outside Windows and Program Files.");
        var child = Path.Combine(root, "pome-" + id.ToString("N"));
        if (Directory.Exists(child) || File.Exists(child)) throw new IOException("This operation already has resources. Inspect its journal before retrying with a new operation.");
        return child;
    }
    public static bool IsWithin(string path, string root) => !string.IsNullOrEmpty(root) &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    public static async Task VerifyHashAsync(string path, string expected, CancellationToken ct = default)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("A SHA-256 digest is required.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var actual = await SHA256.HashDataAsync(file, ct);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected))) throw new InvalidDataException("SHA-256 verification failed. The file will not be used.");
    }
}
