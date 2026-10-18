namespace PasswordManagerLocal.Common.Frontend.Theming;

internal readonly record struct AppThemePreparation(
    AppThemeChangeStatus Status,
    AppThemeCandidate? Candidate);
