namespace PasswordManagerLocal.Windows.Ipc.Contracts.Requests;

/// <summary>
/// Enables or disables notifications and selects the filter for the already-registered UI.
/// When enabled, null means global events only. The user identity is never included in frames.
/// </summary>
public sealed record SetChangeNotificationUserRequestDto(Guid? UserId, bool Enabled);
