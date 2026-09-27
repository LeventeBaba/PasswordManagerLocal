namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Describes only the retained payload row. Durable origin health is stored separately in
/// <see cref="UserSyncFault"/> so an ordinary corrupt revision cannot permanently poison an
/// origin namespace.
/// </summary>
public enum UserSyncSnapshotStatus : byte
{
    Pending = 0,
    LocalPublished = 1,

    /// <summary>A terminal same-origin immutable fork. Later revisions do not clear it.</summary>
    IsolatedFork = 2,

    // Kept as a source-compatible name for strict fork isolation. Database compatibility is
    // intentionally not preserved; new code should use IsolatedFork.
    Quarantined = IsolatedFork,

    MergedReceipt = 3,

    /// <summary>A newer revision received after ordinary corruption, awaiting keyed verification.</summary>
    RecoveryCandidate = 4,

    /// <summary>An individual revision proven semantically corrupt by a trusted key.</summary>
    IsolatedCorrupt = 5,

    /// <summary>Compact retained payload evidence that has been superseded by a healthy revision.</summary>
    SupersededBadEvidence = 6
}
