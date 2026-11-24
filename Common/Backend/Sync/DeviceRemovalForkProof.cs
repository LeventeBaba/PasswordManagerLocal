namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>Two immutable signed publications bound into an explicitly authorized removal.</summary>
public sealed class DeviceRemovalForkProof
{
    public Guid AuthorizationId { get; set; }
    public byte[] FirstEnvelope { get; set; } = [];
    public byte[] SecondEnvelope { get; set; } = [];
}
