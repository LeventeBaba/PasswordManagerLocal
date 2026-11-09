using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class DeviceSyncUiArchitectureTests
{
    [TestMethod]
    public void ListAndDetailsUseConfirmedValuesAndTheApplicationSwitchTheme()
    {
        XNamespace controls = "clr-namespace:PasswordManagerLocal.Common.Frontend.Views.Controls";
        foreach (var name in new[] { "DeviceListView.axaml", "DeviceDetailsView.axaml" })
        {
            var view = XDocument.Load(Path.Combine(Root(), "Common", "Frontend", "Views", "Pages", "Profile", name));
            var toggle = view.Descendants(controls + "ConfirmedToggleSwitch").Single();
            StringAssert.Contains((string)toggle.Attribute("ConfirmedIsChecked")!, "IsSyncOn, Mode=OneWay");
            StringAssert.Contains((string)toggle.Attribute("IsEnabled")!, "CanToggleDeviceSync");
            StringAssert.Contains((string)toggle.Attribute("Command")!, "ToggleDeviceSyncCommand");
            Assert.IsNull(toggle.Attribute("IsChecked"), "Only confirmed state may move these switches.");
            Assert.IsNull(toggle.Attribute("Theme"), "The application-owned switch theme should remain inherited.");
        }
        var source = Read("Common", "Frontend", "Views", "Controls", "ConfirmedToggleSwitch.cs");
        StringAssert.Contains(source, "StyleKeyOverride => typeof(ToggleSwitch)");
        StringAssert.Contains(source, "protected override void Toggle() { }");
        StringAssert.Contains(source, "change.Property == ConfirmedIsCheckedProperty");
        StringAssert.Contains(source, "handledEventsToo: true");
        StringAssert.Contains(source, "_knobsPanel.RemoveHandler(PointerReleasedEvent, HandleKnobReleased)");
        var release = source[source.IndexOf("private void HandleKnobReleased", StringComparison.Ordinal)..];
        Assert.IsTrue(release.IndexOf("SetCurrentValue(IsCheckedProperty, ConfirmedIsChecked)", StringComparison.Ordinal) <
            release.IndexOf("OnClick()", StringComparison.Ordinal), "Drag must restore the confirmed value before requesting a change.");
    }

    [TestMethod]
    public void ConfirmationWrapsTextScrollsItsBodyAndKeepsBothActionsOutsideTheScroller()
    {
        XNamespace av = "https://github.com/avaloniaui";
        var dialog = XDocument.Load(Path.Combine(Root(), "Common", "Frontend", "Views", "Pages", "Profile", "LocalSyncDialogView.axaml"));
        var title = dialog.Descendants(av + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding LocalSyncDialogTitle}");
        Assert.AreEqual("Wrap", (string?)title.Attribute("TextWrapping"));
        var scroller = dialog.Descendants(av + "ScrollViewer").Single();
        Assert.AreEqual("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.IsFalse(scroller.Descendants(av + "Button").Any());
        var actions = dialog.Descendants(av + "Button").ToArray();
        Assert.AreEqual(2, actions.Length);
        foreach (var action in actions)
        {
            Assert.AreEqual("Stretch", (string?)action.Attribute("HorizontalContentAlignment"));
            Assert.AreEqual("Wrap", (string?)action.Element(av + "TextBlock")?.Attribute("TextWrapping"));
        }
        CollectionAssert.AreEquivalent(new[] { "{Binding CanConfirmLocalSyncToggle}", "{Binding CanCancelLocalSyncToggle}" },
            actions.Select(action => (string)action.Attribute("IsEnabled")!).ToArray());
        Assert.IsTrue(scroller.Descendants().Any(element => element.Name.LocalName == "OperationMessageView"));
        Assert.IsTrue(scroller.Descendants(av + "TextBlock").Any(element =>
            (string?)element.Attribute("IsVisible") == "{Binding IsChangingDeviceSync}"));
    }

    [TestMethod]
    public void RefreshDoesNotProjectTheDeviceWideFlagAndEnterRespectsTheFocusedButton()
    {
        var main = Read("Common", "Frontend", "ViewModels", "MainViewModel.cs");
        var start = main.IndexOf("case FrontendRefreshPage.LocalDevice:", StringComparison.Ordinal);
        var end = main.IndexOf("case FrontendRefreshPage.Enrollment:", start, StringComparison.Ordinal);
        var localRefresh = main[start..end];
        StringAssert.Contains(localRefresh, "RefreshLocalUserSyncAutomaticallyAsync");
        Assert.IsFalse(localRefresh.Contains("GetLocalDeviceInfoAsync", StringComparison.Ordinal));
        StringAssert.Contains(main, "\"IsChangingDeviceSync\"");
        var keyboard = Read("Common", "Frontend", "Views", "Behaviors", "MainViewKeyboardHandler.cs");
        StringAssert.Contains(keyboard, "source is Button || source.GetVisualAncestors().OfType<Button>().Any()");
        var dialog = Read("Common", "Frontend", "Views", "Pages", "Profile", "LocalSyncDialogView.axaml.cs");
        StringAssert.Contains(dialog, "FindControl<Button>(\"LocalSyncCancelButton\")?.Focus()");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    private static string Root([CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
