namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IClipboardWriter
{
    Task<bool> TrySetTextAsync(string text);
}
