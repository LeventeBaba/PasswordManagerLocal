namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class SensitiveDataVisibilityService
{
    public event EventHandler? HideVisibleSecretsRequested;

    internal void ClearSubscribers() => HideVisibleSecretsRequested = null;

    public void RequestHideVisibleSecrets()
    {
        HideVisibleSecretsRequested?.Invoke(null, EventArgs.Empty);
    }
}
