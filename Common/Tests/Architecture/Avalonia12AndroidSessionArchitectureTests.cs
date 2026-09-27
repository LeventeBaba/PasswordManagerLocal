using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Avalonia12AndroidSessionArchitectureTests
{
    [TestMethod]
    public void PackageBaselineUsesAvalonia12ReactiveUiAvaloniaAndSkia3()
    {
        var root = GetRepositoryRoot();
        var packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));

        StringAssert.Contains(packages, "<PackageVersion Include=\"Avalonia\" Version=\"12.1.3\" />");
        StringAssert.Contains(packages, "<PackageVersion Include=\"ReactiveUI.Avalonia\" Version=\"12.1.2\" />");
        StringAssert.Contains(packages, "<PackageVersion Include=\"SkiaSharp\" Version=\"3.119.4\" />");
        Assert.IsFalse(packages.Contains("Avalonia.ReactiveUI", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AndroidUsesActivityLifetimeFactoryAndFreshUiSession()
    {
        var root = GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "Android", "Frontend", "PasswordManagerLocalApplication.cs"));
        var activity = File.ReadAllText(Path.Combine(root, "Android", "Frontend", "MainActivity.cs"));
        var commonApp = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "App.axaml.cs"));

        StringAssert.Contains(app, ": AvaloniaAndroidApplication<App>");
        Assert.IsFalse(activity.Contains("CreateAppBuilder", StringComparison.Ordinal));
        Assert.IsFalse(activity.Contains("CustomizeAppBuilder", StringComparison.Ordinal));
        Assert.IsFalse(activity.Contains("ISingleViewApplicationLifetime", StringComparison.Ordinal));
        StringAssert.Contains(activity, "new FrontendUiSession");
        StringAssert.Contains(activity, "DisposeSessionAsync");
        StringAssert.Contains(commonApp, "IActivityApplicationLifetime");
        StringAssert.Contains(commonApp, "MainViewFactory = static () => new MainView()");
        Assert.IsFalse(commonApp.Contains("AuthSessionRegistry AuthSessionRegistry", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActivityBoundPlatformServicesAreSessionInstances()
    {
        var root = GetRepositoryRoot();
        var services = new[]
        {
            "ClipboardService.cs",
            "QrImagePickerService.cs",
            "SoftwareKeyboardService.cs",
            "EnrollmentQrCodeCameraScannerService.cs",
            "SensitiveDataVisibilityService.cs"
        };
        foreach (var name in services)
        {
            var source = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "Services", name));
            Assert.IsFalse(source.Contains("public static class", StringComparison.Ordinal), name);
        }

        var platformServices = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Services", "FrontendPlatformServices.cs"));
        StringAssert.Contains(platformServices, "CancellationToken LifetimeToken");
        StringAssert.Contains(platformServices, "public void Deactivate()");
        StringAssert.Contains(platformServices, "SensitiveData.ClearSubscribers()");
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
