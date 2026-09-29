using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal sealed class RecordingDeviceSecurityService(
    DeviceSecurityService inner, PeerPenaltyRecorder recorder) : IDeviceSecurityService
{
    public async Task RecordInvalidIncomingSyncAsync(Device device, string reason, CancellationToken ct = default)
    {
        await inner.RecordInvalidIncomingSyncAsync(device, reason, ct);
        recorder.Record(device.Id, reason);
    }

    public Task ResetInvalidIncomingSyncAsync(Device device, CancellationToken ct = default) =>
        inner.ResetInvalidIncomingSyncAsync(device, ct);
}
