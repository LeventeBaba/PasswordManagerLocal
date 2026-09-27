using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserDataBundleVerificationResult : IDisposable
{
    public UserDataVerificationState State { get; init; }
    public UserDataBlobKind FailedBlobs { get; init; }
    public UserSyncKeyConfidence KeyConfidence { get; init; }
    public UserDataBundle? VerifiedBundle { get; init; }
    public string DiagnosticCode { get; init; } = string.Empty;
    public bool IsHealthy => State == UserDataVerificationState.Healthy && VerifiedBundle is not null;

    public void Dispose() => VerifiedBundle?.Dispose();
}
