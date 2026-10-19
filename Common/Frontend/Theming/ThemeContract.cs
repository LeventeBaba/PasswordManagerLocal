using Avalonia.Media;

namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>Defines and validates the complete semantic resource contract.</summary>
public static class ThemeContract
{
    public static IReadOnlyList<ThemeResourceKey> RequiredResources { get; } =
        Array.AsReadOnly(Enum.GetValues<ThemeResourceKey>());

    public static string GetResourceName(ThemeResourceKey resource) => resource switch
    {
        ThemeResourceKey.BackgroundPrimary => "AppBrush.Background.Primary",
        ThemeResourceKey.BackgroundSecondary => "AppBrush.Background.Secondary",
        ThemeResourceKey.SurfaceCard => "AppBrush.Surface.Card",
        ThemeResourceKey.SurfaceElevated => "AppBrush.Surface.Elevated",
        ThemeResourceKey.BorderDefault => "AppBrush.Border.Default",
        ThemeResourceKey.Divider => "AppBrush.Divider",
        ThemeResourceKey.TextPrimary => "AppBrush.Text.Primary",
        ThemeResourceKey.TextSecondary => "AppBrush.Text.Secondary",
        ThemeResourceKey.TextDisabled => "AppBrush.Text.Disabled",
        ThemeResourceKey.ActionPrimary => "AppBrush.Action.Primary",
        ThemeResourceKey.ActionPrimaryForeground => "AppBrush.Action.PrimaryForeground",
        ThemeResourceKey.ActionPrimaryDisabledSurface => "AppBrush.Action.Primary.DisabledSurface",
        ThemeResourceKey.ActionSecondary => "AppBrush.Action.Secondary",
        ThemeResourceKey.ActionSecondarySurface => "AppBrush.Action.Secondary.Surface",
        ThemeResourceKey.ActionSecondaryHoverSurface => "AppBrush.Action.Secondary.HoverSurface",
        ThemeResourceKey.ActionSecondaryPressedSurface => "AppBrush.Action.Secondary.PressedSurface",
        ThemeResourceKey.ActionSecondaryDisabledSurface => "AppBrush.Action.Secondary.DisabledSurface",
        ThemeResourceKey.ActionDangerDisabledSurface => "AppBrush.Action.Danger.DisabledSurface",
        ThemeResourceKey.ActionDisabledForeground => "AppBrush.Action.DisabledForeground",
        ThemeResourceKey.Focus => "AppBrush.Focus",
        ThemeResourceKey.SelectionBackground => "AppBrush.Selection.Background",
        ThemeResourceKey.SelectionBorder => "AppBrush.Selection.Border",
        ThemeResourceKey.StatusInformation => "AppBrush.Status.Information",
        ThemeResourceKey.StatusSuccess => "AppBrush.Status.Success",
        ThemeResourceKey.StatusWarning => "AppBrush.Status.Warning",
        ThemeResourceKey.StatusDanger => "AppBrush.Status.Danger",
        ThemeResourceKey.DeviceOnline => "AppBrush.Device.Online",
        ThemeResourceKey.DeviceOffline => "AppBrush.Device.Offline",
        ThemeResourceKey.DeviceBlocked => "AppBrush.Device.Blocked",
        ThemeResourceKey.DialogOverlay => "AppBrush.Dialog.Overlay",
        ThemeResourceKey.NavigationSelected => "AppBrush.Navigation.Selected",
        ThemeResourceKey.NavigationSelectedBorder => "AppBrush.Navigation.SelectedBorder",
        ThemeResourceKey.SurfaceSubtle => "AppBrush.Surface.Subtle",
        ThemeResourceKey.SurfaceInput => "AppBrush.Surface.Input",
        ThemeResourceKey.SurfaceSubtleEmphasis => "AppBrush.Surface.SubtleEmphasis",
        ThemeResourceKey.SurfacePressed => "AppBrush.Surface.Pressed",
        ThemeResourceKey.SurfaceBadge => "AppBrush.Surface.Badge",
        ThemeResourceKey.SurfaceHover => "AppBrush.Surface.Hover",
        ThemeResourceKey.BorderSubtle => "AppBrush.Border.Subtle",
        ThemeResourceKey.BorderSubtleStrong => "AppBrush.Border.SubtleStrong",
        ThemeResourceKey.BorderMuted => "AppBrush.Border.Muted",
        ThemeResourceKey.BorderEmphasis => "AppBrush.Border.Emphasis",
        ThemeResourceKey.ActionDanger => "AppBrush.Action.Danger",
        ThemeResourceKey.ActionDangerForeground => "AppBrush.Action.DangerForeground",
        ThemeResourceKey.NavigationHover => "AppBrush.Navigation.Hover",
        ThemeResourceKey.NavigationHoverBorder => "AppBrush.Navigation.HoverBorder",
        ThemeResourceKey.SelectionHoverBackground => "AppBrush.Selection.HoverBackground",
        ThemeResourceKey.SelectionHoverBorder => "AppBrush.Selection.HoverBorder",
        ThemeResourceKey.StatusDangerSurface => "AppBrush.Status.Danger.Surface",
        ThemeResourceKey.StatusDangerBorder => "AppBrush.Status.Danger.Border",
        ThemeResourceKey.DeviceOnlineSurface => "AppBrush.Device.Online.Surface",
        ThemeResourceKey.DeviceOfflineSurface => "AppBrush.Device.Offline.Surface",
        ThemeResourceKey.DeviceBlockedSurface => "AppBrush.Device.Blocked.Surface",
        ThemeResourceKey.ScrollbarTrack => "AppBrush.Scrollbar.Track",
        ThemeResourceKey.ScrollbarThumb => "AppBrush.Scrollbar.Thumb",
        ThemeResourceKey.ScrollbarThumbHover => "AppBrush.Scrollbar.ThumbHover",
        ThemeResourceKey.ScrollbarThumbPressed => "AppBrush.Scrollbar.ThumbPressed",
        ThemeResourceKey.PasswordStrengthInactive => "AppBrush.PasswordStrength.Inactive",
        ThemeResourceKey.PasswordStrengthWeak => "AppBrush.PasswordStrength.Weak",
        ThemeResourceKey.PasswordStrengthModerate => "AppBrush.PasswordStrength.Moderate",
        ThemeResourceKey.PasswordStrengthGood => "AppBrush.PasswordStrength.Good",
        ThemeResourceKey.PasswordStrengthStrong => "AppBrush.PasswordStrength.Strong",
        ThemeResourceKey.PasswordStrengthExcellent => "AppBrush.PasswordStrength.Excellent",
        ThemeResourceKey.SurfaceCardOverlay => "AppBrush.Surface.CardOverlay",
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null)
    };

    public static IReadOnlyList<ThemeResourceKey> FindViolations(IAppThemeResourceSet resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var violations = new List<ThemeResourceKey>();
        foreach (var resource in RequiredResources)
        {
            if (!resources.TryGetResource(GetResourceName(resource), out var value) ||
                value is not IBrush)
            {
                violations.Add(resource);
            }
        }

        return violations;
    }
}
