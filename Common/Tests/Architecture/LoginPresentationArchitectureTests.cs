using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class LoginPresentationArchitectureTests
{
    [TestMethod]
    public void OptionalShellAndEnrollmentSectionsUseNullGuardedTemplates()
    {
        var views = Path.Combine(GetRepositoryRoot(), "Common", "Frontend", "Views");
        var shell = XDocument.Load(Path.Combine(views, "MainView.axaml"));
        var login = XDocument.Load(Path.Combine(views, "Auth", "LoginView.axaml"));
        foreach (var property in new[] { "DesktopHeaderContent", "MobileHeaderContent",
                     "SessionRenewalDialogContent", "DatabaseRecoveryDialogContent" })
            AssertDeferredSection(shell, property, 1);
        AssertDeferredSection(shell, "AuthenticatedMenuContent", 2);
        foreach (var property in new[] { "DeviceTransferIntroContent", "DeviceTransferCodeContent",
                     "DeviceTransferFinishedContent" })
            AssertDeferredSection(login, property, 1);
    }

    [TestMethod]
    public void EveryFeatureControlViewLoadsItsThemesBeforeInitializingControls()
    {
        var root = GetRepositoryRoot();
        XNamespace avalonia = "https://github.com/avaloniaui";
        var views = Path.Combine(root, "Common", "Frontend", "Views");
        var featureControls = new HashSet<string>(StringComparer.Ordinal)
        {
            "ComboBox", "ComboBoxItem", "TabControl", "TabItem", "ColorView"
        };
        var consumers = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path).Descendants()
                .Any(element => element.Name.Namespace == avalonia && featureControls.Contains(element.Name.LocalName)))
            .ToArray();
        Assert.AreEqual(6, consumers.Length,
            "Review resource ownership when adding another feature control view.");
        foreach (var path in consumers)
        {
            var source = File.ReadAllText(path + ".cs");
            var load = source.IndexOf("DeferredControlThemeResources.EnsureLoaded();", StringComparison.Ordinal);
            var initialize = source.IndexOf("InitializeComponent();", StringComparison.Ordinal);
            Assert.IsTrue(load >= 0 && initialize > load,
                $"{Path.GetFileName(path)} must load its tab/combo-box themes before creating its controls.");
        }

        var app = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "App.axaml.cs"));
        StringAssert.Contains(app, "if (_featureControlThemesLoaded)");
        StringAssert.Contains(app, "Resources.MergedDictionaries.Add(new FeatureControlThemes());");
        var helper = File.ReadAllText(Path.Combine(views, "Styling", "DeferredControlThemeResources.cs"));
        StringAssert.Contains(helper, "Dispatcher.UIThread.VerifyAccess();");
        StringAssert.Contains(helper, "app.EnsureFeatureControlThemes();");
    }

    private static void AssertDeferredSection(XDocument document, string property, int count)
    {
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace styling = "clr-namespace:PasswordManagerLocal.Common.Frontend.Views.Styling";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sections = document.Descendants(avalonia + "ContentControl")
            .Where(control => (string?)control.Attribute("Content") == $"{{Binding {property}}}")
            .ToArray();
        Assert.AreEqual(count, sections.Length, property);
        foreach (var section in sections)
        {
            var wrapper = section.Element(avalonia + "ContentControl.ContentTemplate")!
                .Elements().Single();
            Assert.AreEqual(styling + "NonNullDataTemplate", wrapper.Name,
                "Explicit Avalonia templates can build null content and need a construction guard.");
            var template = wrapper.Element(avalonia + "DataTemplate");
            Assert.IsNotNull(template);
            Assert.IsNotNull(template.Attribute(xaml + "DataType"));
            Assert.AreEqual(0, section.Elements().Count(element =>
                element.Name != avalonia + "ContentControl.ContentTemplate"),
                "Optional controls must be inside the deferred template.");
        }
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
