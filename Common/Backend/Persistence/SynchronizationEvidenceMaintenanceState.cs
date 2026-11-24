namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Process-local, constant-space progress. One existing maintenance owner leases a batch;
/// unchanged capacity refusals cannot restart an exhausted or failed proof generation.
/// </summary>
internal sealed class SynchronizationEvidenceMaintenanceState
{
    private readonly object _gate = new();
    private long _generation;
    private long _completedGeneration = -1;
    private long _passGeneration;
    private long _requestedGeneration;
    private bool _requested = true;
    private bool _inPass;
    private bool _leased;
    private EvidenceCompactionCursor? _cursor;

    public void ProofChanged(bool requestMaintenance = true)
    {
        lock (_gate)
        {
            _generation++;
            if (requestMaintenance) { _requested = true; _requestedGeneration = _generation; }
        }
    }

    public void CapacityPressure()
    {
        lock (_gate)
            if (_completedGeneration != _generation) { _requested = true; _requestedGeneration = _generation; }
    }

    public bool TryBeginBatch(out EvidenceCompactionCursor? cursor)
    {
        lock (_gate)
        {
            cursor = null;
            if (_leased || !_requested) return false;
            if (!_inPass)
            {
                _inPass = true;
                _passGeneration = _generation;
                _cursor = null;
            }
            _leased = true;
            cursor = _cursor;
            return true;
        }
    }

    public void CompleteBatch(EvidenceCompactionCursor? next)
    {
        lock (_gate)
        {
            if (!_leased) throw new InvalidOperationException("No evidence maintenance batch is leased.");
            _leased = false;
            _cursor = next;
            if (next is not null) return;
            FinishPass();
        }
    }

    public void FailBatch()
    {
        lock (_gate)
        {
            if (!_leased) return;
            _leased = false;
            // Preserve all evidence; a new proof generation, rather than a tight retry,
            // supplies the next opportunity after disk/WAL failure or suspended ingress.
            FinishPass();
        }
    }

    private void FinishPass()
    {
        _completedGeneration = _passGeneration;
        _inPass = false;
        _cursor = null;
        _requested = _requestedGeneration > _completedGeneration;
    }
}
