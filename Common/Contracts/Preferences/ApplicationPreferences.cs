namespace PasswordManagerLocal.Common.Contracts.Preferences;

/// <summary>
/// Defines the complete, non-sensitive application-wide preference schema shared by all runtimes.
/// </summary>
public sealed record ApplicationPreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required AppLanguage Language { get; init; }

    public required AppThemeMode Theme { get; init; }
}
