namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IInteractiveSessionLifetime
{
    CancellationToken CurrentLifetime { get; }
}
