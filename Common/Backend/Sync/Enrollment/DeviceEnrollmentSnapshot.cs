using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Constants;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentSnapshot
{
    [JsonRequired]
    public int PayloadVersion { get; set; } = SyncConstants.DeviceEnrollmentPayloadVersion;
    public Guid PrimaryUserId { get; set; }
    public Guid TargetDeviceId { get; set; }
    public Guid TargetOriginInstanceId { get; set; }
    public byte[] TargetSignPublicKeyHash { get; set; } = [];
    public byte[] TargetAgreementPublicKeyHash { get; set; } = [];
    public string TargetTlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType TargetDeviceType { get; set; }
    public Guid AuthorizingAdditionOperationId { get; set; }
    public byte[] AuthorizingAdditionOperationHash { get; set; } = [];
    public List<DeviceEnrollmentUserSnapshot> Users { get; set; } = [];
    public List<DeviceEnrollmentGroupSnapshot> Groups { get; set; } = [];
    public List<DeviceEnrollmentDeviceSnapshot> Devices { get; set; } = [];
    public List<DeviceEnrollmentUserDeviceSnapshot> UserDevices { get; set; } = [];
    public List<DeviceEnrollmentMembershipAuthorizationSnapshot> MembershipAuthorizations { get; set; } = [];
    public List<DeviceEnrollmentRemovalCutoffSnapshot> RemovalCutoffs { get; set; } = [];
    public List<DeviceEnrollmentControlOperationSnapshot> ControlOperations { get; set; } = [];
    public List<DeviceEnrollmentControlStateSnapshot> ControlStates { get; set; } = [];
    public List<DeviceEnrollmentRevisionKnowledgeSnapshot> RevisionKnowledge { get; set; } = [];
    public List<DeviceEnrollmentPendingSnapshot> PendingSnapshots { get; set; } = [];
}
