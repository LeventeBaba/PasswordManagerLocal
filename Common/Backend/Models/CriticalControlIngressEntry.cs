namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record CriticalControlIngressEntry(Guid UserId, Guid OperationId, byte[] OperationHash,
    Guid TransportPeerDeviceId, byte[] EnvelopePayload);
