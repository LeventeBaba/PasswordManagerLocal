namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>
/// Tracks the last successfully applied networking configuration independently of
/// observations made by the OS event handler and the fallback poller.
/// This state is owned by SyncNetworkRefreshHostedService's lock.
/// </summary>
internal sealed class NetworkSignatureChangeTracker
{
    private string _appliedSignature;
    private bool _appliedAvailability;
    private bool _unappliedTransition;
    private long _observationVersion;

    public NetworkSignatureChangeTracker(string initialSignature, bool initialAvailability)
    {
        _appliedSignature = initialSignature;
        _appliedAvailability = initialAvailability;
    }

    public long ObservationVersion => _observationVersion;

    public bool HasUnappliedTransition => _unappliedTransition;

    /// <summary>
    /// Records a genuine signature change, or a network-availability transition
    /// reported by the OS. Remembering intermediate changes is intentional: if
    /// an interface disappears and returns during the debounce, its sockets may
    /// still require rebinding even if its final addresses match the old ones.
    /// </summary>
    public bool Observe(string signature, bool isNetworkEvent = false, bool? reportedAvailability = null)
    {
        var changed = !string.Equals(_appliedSignature, signature, StringComparison.Ordinal)
            || (reportedAvailability.HasValue && reportedAvailability.Value != _appliedAvailability)
            // When enumeration fails we cannot safely declare an OS event spurious.
            || (isNetworkEvent && (signature == "unknown" || _appliedSignature == "unknown"));

        if (changed)
        {
            _unappliedTransition = true;
            _observationVersion++;
        }

        return changed;
    }

    public bool RequiresRefresh(string currentSignature) =>
        _unappliedTransition ||
        !string.Equals(_appliedSignature, currentSignature, StringComparison.Ordinal);

    /// <summary>
    /// Advance the baseline only after all network services have restarted.
    /// An event observed during the restart must not be inadvertently forgotten.
    /// </summary>
    public void MarkRefreshed(string currentSignature, bool currentAvailability, long startedAtVersion)
    {
        _appliedSignature = currentSignature;
        _appliedAvailability = currentAvailability;
        if (_observationVersion == startedAtVersion)
            _unappliedTransition = false;
    }
}
