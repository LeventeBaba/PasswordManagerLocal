using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidDeferredBackgroundSyncSettingsClient : IBackgroundSyncSettingsClient
{
    private readonly AndroidActivityServiceAttachmentHandle _attachmentHandle;

    public AndroidDeferredBackgroundSyncSettingsClient(
        AndroidActivityServiceAttachmentHandle attachmentHandle)
    {
        _attachmentHandle = attachmentHandle
            ?? throw new ArgumentNullException(nameof(attachmentHandle));
    }

    public async Task<BackgroundSyncClientState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        var attachment = await _attachmentHandle.GetAttachmentAsync(cancellationToken);
        return await attachment.BackgroundSyncSettingsClient.GetStateAsync(cancellationToken);
    }

    public async Task<BackgroundSyncChangeResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var attachment = await _attachmentHandle.GetAttachmentAsync(cancellationToken);
        return await attachment.BackgroundSyncSettingsClient.SetEnabledAsync(
            isEnabled,
            cancellationToken);
    }
}
