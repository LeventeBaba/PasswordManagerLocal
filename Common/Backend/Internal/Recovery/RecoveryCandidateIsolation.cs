using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

internal sealed record RecoveryCandidateIsolation(UserSyncSnapshot Row, UserSyncFaultKind Kind,
    string DiagnosticCode, UserDataBlobKind FailedBlobs);
