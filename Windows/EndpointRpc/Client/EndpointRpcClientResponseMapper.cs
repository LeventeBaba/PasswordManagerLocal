using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal static class EndpointRpcClientResponseMapper
{
    public static EndpointRpcTransportResponse RequireSuccess(EndpointRpcTransportResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ResponseKind != EndpointRpcResponseKind.Failure)
            return response;

        throw new EndpointRpcRemoteException(
            response.Error ?? throw new EndpointRpcPayloadException(
                "The endpoint RPC failure response does not contain an error payload."));
    }
}
