using Android.Content;
using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidRuntimeServiceConnector
{
    private static readonly TimeSpan ServiceBindTimeout = TimeSpan.FromSeconds(15);
    public async Task<AndroidActivityServiceAttachment> AttachInteractiveClientAsync(
        Context context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bindingContext = context.ApplicationContext ?? context;
        var connection = new AndroidRuntimeServiceConnection();
        var intent = new Intent(bindingContext, typeof(PasswordManagerBackgroundService));
        if (!bindingContext.BindService(intent, connection, Bind.AutoCreate))
        {
            connection.Dispose();
            throw new InvalidOperationException("The Android runtime service could not be bound.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ServiceBindTimeout);

        try
        {
            AndroidServiceFrontendBackendClient? backendClient = null;
            var handedOff = false;
            try
            {
                var service = await connection.WaitForServiceAsync(timeoutSource.Token);
                backendClient = await service.AttachInteractiveClientAsync(timeoutSource.Token);
                var attachment = new AndroidActivityServiceAttachment(
                    bindingContext,
                    connection,
                    service,
                    backendClient);
                handedOff = true;
                return attachment;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The Android runtime service did not finish activity attachment in time.");
            }
            finally
            {
                // If binding or Activity teardown interrupts after the service has
                // opened the interactive lease, release that lease before unbinding.
                // The returned attachment owns this client on the success path.
                if (backendClient is not null && !handedOff)
                {
                    try { await backendClient.DisposeAsync(); }
                    catch { }
                }
            }
        }
        catch
        {
            try
            {
                bindingContext.UnbindService(connection);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }

            connection.Dispose();
            throw;
        }
    }
}
