namespace ReDows.Core.Backup;

/// <summary>
/// Reads a source file's bytes, READ-ONLY. The implementation must never write,
/// move or delete on the scanned source (invariant #3). It only opens for read.
/// </summary>
public interface ICopySource
{
    Stream OpenRead(string path);
}

/// <summary>
/// Writes files into the chosen backup destination. <see cref="OpenWrite"/> creates any
/// parent directories and truncates; <see cref="OpenReadBack"/> re-opens a just-written
/// file so the engine can verify the destination holds exactly what it copied.
/// A path destination (local disk / USB / UNC network share) is the V1 implementation;
/// FTP / web / cloud are future sinks behind this same interface.
/// </summary>
public interface IBackupSink
{
    Stream OpenWrite(string relativePath);

    Stream OpenReadBack(string relativePath);
}

/// <summary>
/// Receives the capture:secret files into an encrypted vault (a password-protected archive).
/// Disposing finalizes the vault. Secret files go ONLY here, never in clear to the plain copy.
/// </summary>
public interface IVaultSink : IDisposable
{
    void Add(string relativePath, Stream content);
}

/// <summary>One manifest item that could not be copied. It is counted, never silently dropped.</summary>
public sealed record CopyFailure(string Path, string Reason);

/// <summary>
/// The SHA-256 of one copied file, keyed by its backup-relative path. Recorded at backup time (the hash
/// is already computed to verify the copy) so a later restore can prove each restored file is byte-identical
/// to the original that was backed up, even if the backup medium degraded in between.
/// </summary>
public sealed record FileHash(string RelativePath, string Sha256);

/// <summary>
/// The outcome of a copy pass. Total-accounting invariant: every manifest entry lands in
/// exactly one bucket: copied, directory, secret-vaulted, secret-deferred (when no vault
/// password was given) or failed. So <see cref="Unaccounted"/> must be 0. <see cref="Hashes"/>
/// carries each plain-copied file's checksum so a restore can verify it end-to-end.
/// </summary>
public sealed record CopyReport(
    long TotalEntries,
    long FilesCopied,
    long BytesCopied,
    long FilesVerified,
    long Directories,
    long SecretsVaulted,
    long SecretBytesVaulted,
    long SecretsDeferred,
    long SecretBytesDeferred,
    IReadOnlyList<CopyFailure> Failures,
    IReadOnlyList<FileHash> Hashes)
{
    public long Accounted => FilesCopied + Directories + SecretsVaulted + SecretsDeferred + Failures.Count;

    public long Unaccounted => TotalEntries - Accounted;

    /// <summary>
    /// The accounting equation as text ("copied + dirs + vaulted + deferred + failed = N accounted vs M
    /// entries"). Shared by both front-ends so they can never print a different accounting for the same
    /// backup; each appends its own "✓" / "UNACCOUNTED" verdict suffix.
    /// </summary>
    public string AccountingEquation =>
        $"{FilesCopied} + {Directories} + {SecretsVaulted} + {SecretsDeferred} + {Failures.Count} " +
        $"= {Accounted:N0} of {TotalEntries:N0} entries";

    /// <summary>V1 limits, stated rather than hidden (deny-list §0-5).</summary>
    public static readonly IReadOnlyList<string> V1Limits =
    [
        "Secret files go into the encrypted vault when you give a vault password. Without one they are counted and skipped, never copied in the clear.",
        "A copied file's size is its logical size; alternate data streams and on-disk compression are not carried over.",
        "A file kept online-only in the cloud is copied by reading it, which can download it. A later version will skip files that are already synced.",
        "A file locked by another program is rescued from a volume shadow copy when the backup runs as administrator. Without that, it is reported as a failure, never skipped silently.",
    ];
}
