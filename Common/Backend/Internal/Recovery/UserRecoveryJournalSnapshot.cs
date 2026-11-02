using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

/// <summary>Profile-scoped encrypted recovery evidence. Never contains installation private keys.</summary>
internal sealed class UserRecoveryJournalSnapshot
{
    public int FormatVersion { get; set; } = 1;
    public Guid JournalId { get; set; }
    public long TargetKeyEpoch { get; set; }
    public bool Confirmed { get; set; }
    public bool EvidenceOnly { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DeviceEnrollmentUserSnapshot User { get; set; } = new();
    public byte[]? SavedKey { get; set; }
    public bool HasLocalLink { get; set; }
    public Guid LocalDeviceIdentityId { get; set; }
    public bool LocalSyncOn { get; set; }
    public byte[] LocalLinkIntegrityHash { get; set; } = [];
    public List<DeviceEnrollmentUserDeviceSnapshot> DeviceLinks { get; set; } = [];
    public List<UserMembershipAuthorization> Authorizations { get; set; } = [];
    public List<UserControlOperation> Operations { get; set; } = [];
    public List<UserOriginRemovalCutoff> Cutoffs { get; set; } = [];
    public List<UserSyncSnapshot> Snapshots { get; set; } = [];
    public List<UserRevisionKnowledge> Knowledge { get; set; } = [];
    public List<DeviceEnrollmentCommit> EnrollmentCommits { get; set; } = [];
    public List<UserSyncFault> Faults { get; set; } = [];
    public UserControlState? ControlState { get; set; }
    public UserSyncState? SyncState { get; set; }
    public UserCanonicalCheckpoint? Checkpoint { get; set; }
}
