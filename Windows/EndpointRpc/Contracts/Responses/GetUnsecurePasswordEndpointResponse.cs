namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetUnsecurePasswordEndpointResponse
{
    public byte[] Password { get; set; } = [];
}
