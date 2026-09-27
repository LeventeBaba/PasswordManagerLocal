namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

/// <summary>
/// Serializes lifecycle-sensitive work for one user while allowing nested calls in the same
/// asynchronous execution flow. Implementations must not retain unused user keys indefinitely.
/// </summary>
public interface IUserLifecycleCoordinator
{
    Task ExecuteAsync(
        Guid userId,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default);

    Task<T> ExecuteAsync<T>(
        Guid userId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default);
}
