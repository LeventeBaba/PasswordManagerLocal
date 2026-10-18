namespace PasswordManagerLocal.Common.Contracts.Preferences;

/// <summary>
/// Stable identifiers for complete, independently selectable application themes.
/// Each value names a complete theme; brightness is frontend metadata rather than
/// a second persisted preference.
/// </summary>
public enum AppThemeId
{
    Dark,
    Light
}
