using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class ConfiguredEndpointFailureEndpoints : ThrowingRecordingEndpoints
{
    private readonly EndpointOperationId _operationId;
    private readonly Exception _failure;

    public ConfiguredEndpointFailureEndpoints(
        EndpointOperationId operationId,
        Exception failure)
    {
        _operationId = operationId;
        _failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public int InvocationCount { get; private set; }

    public override Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        if (_operationId != EndpointOperationId.Logout)
            return base.LogoutAsync(token, ct);
        InvocationCount++;
        return Task.FromException(_failure);
    }

    public override Task ChangeMasterPasswordAsync(
        MasterPasswordChangeRequest request,
        CancellationToken ct = default)
    {
        if (_operationId != EndpointOperationId.ChangeMasterPassword)
            return base.ChangeMasterPasswordAsync(request, ct);
        InvocationCount++;
        return Task.FromException(_failure);
    }

    public override Task AddDeviceByCodeAsync(
        Guid token,
        string code,
        CancellationToken ct = default)
    {
        if (_operationId != EndpointOperationId.AddDeviceByCode)
            return base.AddDeviceByCodeAsync(token, code, ct);
        InvocationCount++;
        return Task.FromException(_failure);
    }

    public override Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(
        CancellationToken ct = default)
    {
        if (_operationId != EndpointOperationId.GetLocalDeviceInfo)
            return base.GetLocalDeviceInfoAsync(ct);
        InvocationCount++;
        return Task.FromException<LocalDeviceInfoResponse>(_failure);
    }
}
