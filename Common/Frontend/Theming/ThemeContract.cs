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
        ThemeResourceKey.ActionSecondary => "AppBrush.Action.Secondary",
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
