namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class DeviceRemovalResultResponse
{
    public bool Removed { get; set; }
    public bool AllKnownOriginRevisionsMerged { get; set; }
    public bool MayContainUnobservedChanges { get; set; }
    public string Message { get; set; } = string.Empty;
    public Guid? OperationId { get; set; }
    public long ResultingMembershipEpoch { get; set; }
}
