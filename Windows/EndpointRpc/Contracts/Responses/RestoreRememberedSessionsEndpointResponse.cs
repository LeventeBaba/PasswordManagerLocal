namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class RestoreRememberedSessionsEndpointResponse
{
    public IReadOnlyList<Guid> Tokens { get; set; } = [];
}
