namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class DeleteCustomUserColorsEndpointRequest
{
    public Guid Token { get; set; }
    public IReadOnlyList<Guid> CustomUserColorIds { get; set; } = [];
}
