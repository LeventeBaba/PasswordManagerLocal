namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointRpcErrorCategory
{
    Authentication = 1,
    Authorization = 2,
    Validation = 3,
    NotFound = 4,
    Conflict = 5,
    Availability = 6,
    Cancellation = 7,
    Internal = 8,
    Recovery = 9
}
