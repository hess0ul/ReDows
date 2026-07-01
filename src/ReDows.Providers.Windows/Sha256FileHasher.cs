using System.IO;
using System.Security.Cryptography;
using ReDows.Core.Duplicates;

namespace ReDows.Providers.Windows;

/// <summary>
/// The real <see cref="IFileHasher"/>: SHA-256 over file bytes, read-only. Opens with a permissive
/// share mode so a file another process is using can still be read (best-effort), and with the
/// SEQUENTIAL-SCAN hint so Windows trims already-read pages instead of filling standby ("cached")
/// memory with the whole disk while hashing — the tool's own footprint stays modest. A file that
/// cannot be read (locked, denied, vanished) yields null — the finder then leaves it out of any
/// group. The partial hash covers the first <see cref="PrefixBytes"/> only, so files that differ
/// near the start are separated without reading them whole.
/// </summary>
public sealed class Sha256FileHasher : IFileHasher
{
    private const int PrefixBytes = 64 * 1024;

    public string? PartialHash(string path) => Hash(path, PrefixBytes);

    public string? FullHash(string path) => Hash(path, wholeFile: null);

    private static string? Hash(string path, int? wholeFile)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
                BufferSize = PrefixBytes,
            });
            using var sha = SHA256.Create();

            if (wholeFile is null)
            {
                return Convert.ToHexString(sha.ComputeHash(stream));
            }

            var buffer = new byte[wholeFile.Value];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return Convert.ToHexString(sha.ComputeHash(buffer, 0, total));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
