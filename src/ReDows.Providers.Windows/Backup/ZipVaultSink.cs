using ICSharpCode.SharpZipLib.Zip;
using ReDows.Core.Backup;

namespace ReDows.Providers.Windows.Backup;

/// <summary>
/// The encrypted secrets vault: a password-protected ZIP with WinZip AES-256 entries,
/// openable by any standard tool (7-Zip, WinRAR…) on any machine with the passphrase — so
/// the secrets are recoverable after the reset even without ReDows. Secret files are streamed
/// straight in; they are never written in clear to the destination (invariant #5).
/// </summary>
public sealed class ZipVaultSink : IVaultSink
{
    private readonly ZipOutputStream _zip;

    public ZipVaultSink(Stream output, string passphrase)
    {
        _zip = new ZipOutputStream(output) { Password = passphrase, IsStreamOwner = true };
        _zip.SetLevel(6);
    }

    public int AddedCount { get; private set; }

    public void Add(string relativePath, Stream content)
    {
        var entry = new ZipEntry(relativePath) { AESKeySize = 256, DateTime = DateTime.Now };
        _zip.PutNextEntry(entry);
        content.CopyTo(_zip);
        _zip.CloseEntry();
        AddedCount++;
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>
    /// Re-open the finished vault with the passphrase and fully read every entry (which AES-
    /// decrypts and CRC-checks it): proves the vault opens and its contents are intact. Returns
    /// the number of verified entries; throws if the passphrase is wrong or an entry is corrupt.
    /// </summary>
    public static int Verify(string vaultPath, string passphrase)
    {
        using var zip = new ZipFile(vaultPath) { Password = passphrase };
        var verified = 0;
        foreach (ZipEntry entry in zip)
        {
            if (!entry.IsFile)
            {
                continue;
            }

            using var stream = zip.GetInputStream(entry);
            stream.CopyTo(Stream.Null);
            verified++;
        }

        return verified;
    }

    /// <summary>
    /// Open the vault with the passphrase and hand each secret (its stored relative name + a decrypting
    /// content stream) to <paramref name="onEntry"/>, so a restore can write it back to disk. Read-only
    /// on the vault; throws if the passphrase is wrong or an entry is corrupt.
    /// </summary>
    public static void ExtractEach(string vaultPath, string passphrase, Action<string, Stream> onEntry)
    {
        using var zip = new ZipFile(vaultPath) { Password = passphrase };
        foreach (ZipEntry entry in zip)
        {
            if (!entry.IsFile)
            {
                continue;
            }

            using var stream = zip.GetInputStream(entry);
            onEntry(entry.Name, stream);
        }
    }
}
