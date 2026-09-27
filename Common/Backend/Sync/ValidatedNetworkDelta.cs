using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed record ValidatedNetworkDelta(Device SourceDevice, SyncDeltaPayload Payload);
