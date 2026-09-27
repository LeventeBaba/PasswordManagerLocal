using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class ClipboardService
{
    private readonly CancellationToken _lifetime;

    public ClipboardService(CancellationToken lifetime = default) => _lifetime = lifetime;

    private readonly SemaphoreSlim ClipboardSemaphore = new(1, 1);
    private WeakReference<TopLevel>? _activeTopLevel;
    private IClipboardWriter? _platformClipboardWriter;

    public void SetActiveTopLevel(TopLevel? topLevel)
    {
        _activeTopLevel = topLevel is null ? null : new WeakReference<TopLevel>(topLevel);
    }



    public void SetPlatformClipboardWriter(IClipboardWriter? platformClipboardWriter)
    {
        _platformClipboardWriter = platformClipboardWriter;
    }



    public async Task<bool> TrySetTextAsync(string? text)
    {
        try { return await SetTextCoreAsync(text); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<bool> SetTextCoreAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        await ClipboardSemaphore.WaitAsync(_lifetime);

        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (await TrySetTextWithPlatformClipboardAsync(text))
                {
                    return true;
                }

                if (await TrySetTextWithAvaloniaClipboardAsync(text))
                {
                    return true;
                }

                await Task.Delay(75, _lifetime);
            }
        }
        finally
        {
            ClipboardSemaphore.Release();
        }

        return false;
    }



    public static string? GetSelectedText(TextBox textBox) =>
        string.IsNullOrEmpty(textBox.SelectedText)
            ? null
            : textBox.SelectedText;



    private async Task<bool> TrySetTextWithPlatformClipboardAsync(string text)
    {
        if (_lifetime.IsCancellationRequested || _platformClipboardWriter is not { } writer)
        {
            return false;
        }

        try
        {
            return await writer.TrySetTextAsync(text).WaitAsync(_lifetime);
        }
        catch
        {
            return false;
        }
    }



    private async Task<bool> TrySetTextWithAvaloniaClipboardAsync(string text)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return await TrySetTextWithAvaloniaClipboardOnUiThreadAsync(text);
            }

            return await Dispatcher.UIThread.InvokeAsync(async () =>
                await TrySetTextWithAvaloniaClipboardOnUiThreadAsync(text));
        }
        catch
        {
            return false;
        }
    }



    private async Task<bool> TrySetTextWithAvaloniaClipboardOnUiThreadAsync(string text)
    {
        _lifetime.ThrowIfCancellationRequested();
        var clipboard = GetClipboard();

        if (clipboard is null)
        {
            return false;
        }

        if (await TrySetTextWithAvaloniaSetTextAsync(clipboard, text))
        {
            return true;
        }

        return await TrySetTextWithAvaloniaDataTransferAsync(clipboard, text);
    }



    private async Task<bool> TrySetTextWithAvaloniaSetTextAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
            await TryFlushAsync(clipboard);
            return true;
        }
        catch
        {
            return false;
        }
    }



    private async Task<bool> TrySetTextWithAvaloniaDataTransferAsync(IClipboard clipboard, string text)
    {
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
            await TryFlushAsync(clipboard);
            return true;
        }
        catch
        {
            return false;
        }
    }



    private async Task TryFlushAsync(IClipboard clipboard)
    {
        try
        {
            await clipboard.FlushAsync();
        }
        catch
        {
        }
    }



    private IClipboard? GetClipboard()
    {
        if (_activeTopLevel?.TryGetTarget(out var activeTopLevel) == true
            && activeTopLevel.Clipboard is { } activeTopLevelClipboard)
        {
            return activeTopLevelClipboard;
        }

        return null;
    }
}
