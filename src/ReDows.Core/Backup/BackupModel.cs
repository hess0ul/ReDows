namespace ReDows.Core.Backup;

/// <summary>
/// Reads a source file's bytes, READ-ONLY. The implementation must never write,
/// move or delete on the scanned source (invariant #3) — it only opens for read.
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

/// <summary>One manifest item that could not be copied — counted, never silently dropped.</summary>
public sealed record CopyFailure(string Path, string Reason);

/// <summary>
/// The outcome of a copy pass. Total-accounting invariant: every manifest entry lands in
/// exactly one bucket — copied, directory, secret-vaulted, secret-deferred (when no vault
/// password was given) or failed — so <see cref="Unaccounted"/> must be 0.
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
    IReadOnlyList<CopyFailure> Failures)
{
    public long Accounted => FilesCopied + Directories + SecretsVaulted + SecretsDeferred + Failures.Count;

    public long Unaccounted => TotalEntries - Accounted;

    /// <summary>V1 limits, stated rather than hidden (deny-list §0-5).</summary>
    public static readonly IReadOnlyList<string> V1Limits =
    [
        "capture:secret files go into the encrypted vault when --vault-password is given; without it they are counted and deferred, never copied in clear.",
        "A copied file's bytes are its logical size; alternate data streams and on-disk compression are not carried.",
        "A cloud placeholder is copied by reading it, which can trigger hydration (a download); a later increment will skip already-synced placeholders instead.",
        "A file locked by another process may fail to open and is reported as a failure (never a silent skip); a shadow-copy read is a later increment.",
    ];
}
