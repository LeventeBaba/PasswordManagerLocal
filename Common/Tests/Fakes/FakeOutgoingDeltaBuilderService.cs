using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeOutgoingDeltaBuilderService : IOutgoingDeltaBuilderService
{
    public NetworkDelta Result { get; set; } = new();
    public SyncItem? LastItem { get; private set; }
    public Device? LastDevice { get; private set; }
    public UserControlOperation? LastControlOperation { get; private set; }
    public Exception? ExceptionToThrow { get; set; }

    public Task<NetworkDelta> BuildAsync(SyncItem item, Device device, CancellationToken ct = default)
    {
        LastItem = item;
        LastDevice = device;
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(Result);
    }
    public Task<NetworkDelta> BuildUserSnapshotRelayAsync(UserSyncSnapshot snapshot, Device device, CancellationToken ct = default)
    {
        LastDevice = device;
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(Result);
    }

    public Task<NetworkDelta> BuildUserControlOperationRelayAsync(UserControlOperation operation, Device device, CancellationToken ct = default)
    {
        LastControlOperation = operation;
        LastDevice = device;
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(Result);
    }

}
