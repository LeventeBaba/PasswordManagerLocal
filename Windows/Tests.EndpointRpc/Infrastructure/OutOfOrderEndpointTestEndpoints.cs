using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

internal sealed class OutOfOrderEndpointTestEndpoints : ThrowingRecordingEndpoints
{
    private readonly TaskCompletionSource _logoutStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseLogout = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task LogoutStarted => _logoutStarted.Task;

    public override async Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        _logoutStarted.TrySetResult();
        await _releaseLogout.Task.WaitAsync(ct);
    }

    public override Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(
        CancellationToken ct = default) =>
        Task.FromResult(new LocalDeviceInfoResponse
        {
            DeviceId = EndpointRpcTestData.ItemId,
            TlsCertFingerprint = "phase9-real-pipe",
            DeviceType = PasswordManagerLocal.Common.Contracts.Devices.DeviceType.WindowsPc,
            CreatedAt = DateTimeOffset.UnixEpoch
        });

    public void ReleaseLogout() => _releaseLogout.TrySetResult();
}
