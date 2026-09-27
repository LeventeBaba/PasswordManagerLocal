using Android.Content;
using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidActivityServiceAttachment : IAsyncDisposable
{
    private readonly Context _bindingContext;
    private readonly AndroidRuntimeServiceConnection _connection;
    private int _disposed;

    public AndroidActivityServiceAttachment(
        Context bindingContext,
        AndroidRuntimeServiceConnection connection,
        PasswordManagerBackgroundService service,
        AndroidServiceFrontendBackendClient backendClient)
    {
        _bindingContext = bindingContext ?? throw new ArgumentNullException(nameof(bindingContext));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(service);
        BackendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
        BackgroundSyncSettingsClient = new AndroidServiceBackgroundSyncSettingsClient(
            service,
            backendClient,
            () => Volatile.Read(ref _disposed) != 0);
    }

    public IFrontendBackendClient<IEndpoints> BackendClient { get; }
    public IBackgroundSyncSettingsClient BackgroundSyncSettingsClient { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await BackendClient.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _bindingContext.UnbindService(_connection);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }
        finally
        {
            _connection.Dispose();
        }

        if (failure is not null)
            throw failure;
    }
}
