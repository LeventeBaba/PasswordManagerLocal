using Android.Content;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidActivityServiceAttachmentHandle : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly object _gate = new();
    private readonly AndroidRuntimeServiceConnector _connector;
    private readonly Context _context;
    private Task<AndroidActivityServiceAttachment>? _attachmentTask;
    private int _disposed;

    public AndroidActivityServiceAttachmentHandle(
        AndroidRuntimeServiceConnector connector,
        Context context)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(context);
        _connector = connector;
        _context = context.ApplicationContext ?? throw new InvalidOperationException(
            "The Android application context is unavailable.");
        BackendClient = new AndroidDeferredServiceFrontendBackendClient(this);
        BackgroundSyncSettingsClient = new AndroidDeferredBackgroundSyncSettingsClient(this);
    }

    public IFrontendBackendClient<IEndpoints> BackendClient { get; }
    public IBackgroundSyncSettingsClient BackgroundSyncSettingsClient { get; }

    internal Task<AndroidActivityServiceAttachment> GetAttachmentAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // Lazy binding keeps Activity creation free of backend startup work. An
            // explicit resume/reconnect may retry an earlier locked-device/bind failure.
            if (_attachmentTask is null || _attachmentTask.IsFaulted || _attachmentTask.IsCanceled)
                _attachmentTask = _connector.AttachInteractiveClientAsync(_context, _lifetimeSource.Token);
            return _attachmentTask.WaitAsync(cancellationToken);
        }
    }

    internal bool TryGetCompletedAttachment(out AndroidActivityServiceAttachment? attachment)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0 && _attachmentTask is { IsCompletedSuccessfully: true })
            {
                attachment = _attachmentTask.Result;
                return true;
            }
        }

        attachment = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeSource.Cancel();
        try
        {
            Task<AndroidActivityServiceAttachment>? pending;
            lock (_gate)
                pending = _attachmentTask;
            if (pending is not null)
            {
                var attachment = await pending;
                await attachment.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            _lifetimeSource.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AndroidActivityServiceAttachmentHandle));
    }
}
