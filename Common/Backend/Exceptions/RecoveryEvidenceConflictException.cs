namespace PasswordManagerLocal.Common.Backend.Exceptions;

/// <summary>
/// Raised when individually authenticated recovery snapshots disagree about immutable
/// same-epoch key material. The conflict is deliberately fail-closed because recovery cannot
/// safely choose one branch without an explicit control-plane resolution.
/// </summary>
public sealed class RecoveryEvidenceConflictException : InvalidOperationException
{
    public RecoveryEvidenceConflictException(string diagnosticCode)
        : base("Authenticated recovery evidence contains an unresolved same-epoch conflict.")
    {
        DiagnosticCode = string.IsNullOrWhiteSpace(diagnosticCode)
            ? "recovery-evidence-conflict"
            : diagnosticCode;
    }

    public string DiagnosticCode { get; }
}
