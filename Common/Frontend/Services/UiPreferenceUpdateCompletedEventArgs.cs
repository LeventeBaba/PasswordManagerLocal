namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class UiPreferenceUpdateCompletedEventArgs(
    UiPreferenceUpdateResult result) : EventArgs
{
    public UiPreferenceUpdateResult Result { get; } = result;
}
