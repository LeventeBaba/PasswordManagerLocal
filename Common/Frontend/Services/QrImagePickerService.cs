using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class QrImagePickerService
{
    private WeakReference<TopLevel>? _activeTopLevel;
    private readonly CancellationToken _lifetime;

    public QrImagePickerService(CancellationToken lifetime = default) => _lifetime = lifetime;

    public void SetActiveTopLevel(TopLevel? topLevel)
    {
        _activeTopLevel = topLevel is null ? null : new WeakReference<TopLevel>(topLevel);
    }



    public async Task<byte[]?> PickImageBytesAsync(string title, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await PickImageBytesOnUiThreadAsync(title, cancellationToken);
        }

        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await PickImageBytesOnUiThreadAsync(title, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task;
    }



    private async Task<byte[]?> PickImageBytesOnUiThreadAsync(string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topLevel = GetTopLevel();
        var storageProvider = topLevel?.StorageProvider;

        if (storageProvider?.CanOpen != true)
        {
            return null;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return null;
            }

            await using var input = await file.OpenReadAsync();
            using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellationToken);
            return output.ToArray();
        }
        finally
        {
            foreach (var file in files)
                file.Dispose();
        }
    }



    private TopLevel? GetTopLevel()
    {
        if (_activeTopLevel?.TryGetTarget(out var activeTopLevel) == true)
        {
            return activeTopLevel;
        }

        return null;
    }
}
