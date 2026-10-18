namespace PasswordManagerLocal.Common.Frontend.Theming;

public sealed record AppThemeDescriptor(
    AppThemeId Id,
    ThemeBrightness Brightness,
    string DisplayNameResourceKey);
