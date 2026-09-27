namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncControlledHostedService : IBackendHostedService
{
    int StartOrder { get; }
}
