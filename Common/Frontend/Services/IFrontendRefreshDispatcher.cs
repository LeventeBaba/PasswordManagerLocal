namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Marshals frontend refresh work to the UI thread.</summary>
public interface IFrontendRefreshDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken);
}
