using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase10AndroidArchitectureGuardTests
{
    [TestMethod]
    public void ServiceCompositionIsTheOnlyAndroidRuntimeFactoryCaller()
    {
        var root = GetRepositoryRoot();
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Where(path => !path.Contains(".Test", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "AndroidBackendRuntimeFactory.Create",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                Path.Combine(
                    "Android",
                    "Frontend",
                    "Runtime",
                    "PasswordManagerBackgroundService.cs")
            },
            callers);
    }

    [TestMethod]
    public void ActivityAndApplicationOwnNoRuntimeOrFallbackComposition()
    {
        var root = GetRepositoryRoot();
        var activity = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "MainActivity.cs"));
        var application = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "PasswordManagerLocalApplication.cs"));
        var combined = activity + application;

        Assert.IsFalse(combined.Contains("AndroidBackendRuntimeFactory", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("BackendRuntimeLifetimeCoordinator", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("new InProcessFrontendBackendClient", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("IBackendRuntime ", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("BackgroundSyncSettingsStore", StringComparison.Ordinal));
        StringAssert.Contains(activity, "AndroidActivityServiceAttachmentHandle");
        StringAssert.Contains(application, "AndroidRuntimeServiceConnector");
    }

    [TestMethod]
    public void AndroidManifestDeclaresConnectedDeviceForegroundServiceAndMinimalRestorationReceivers()
    {
        var manifestPath = Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Properties",
            "AndroidManifest.xml");
        var document = XDocument.Load(manifestPath);
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var permissions = document.Root!
            .Elements("uses-permission")
            .Select(element => element.Attribute(android + "name")?.Value)
            .ToArray();
        var service = document.Descendants("service").Single(element =>
            element.Attribute(android + "name")?.Value.EndsWith(
                "PasswordManagerBackgroundService",
                StringComparison.Ordinal) == true);
        var receiver = document.Descendants("receiver").Single(element =>
            element.Attribute(android + "name")?.Value.EndsWith(
                "AndroidBackgroundRestorationReceiver",
                StringComparison.Ordinal) == true);
        var actions = receiver.Descendants("action")
            .Select(element => element.Attribute(android + "name")?.Value)
            .ToArray();

        CollectionAssert.Contains(permissions, "android.permission.FOREGROUND_SERVICE");
        CollectionAssert.Contains(permissions, "android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE");
        CollectionAssert.Contains(permissions, "android.permission.POST_NOTIFICATIONS");
        CollectionAssert.Contains(permissions, "android.permission.RECEIVE_BOOT_COMPLETED");
        Assert.AreEqual("false", service.Attribute(android + "exported")?.Value);
        Assert.AreEqual("false", service.Attribute(android + "stopWithTask")?.Value);
        Assert.AreEqual("connectedDevice", service.Attribute(android + "foregroundServiceType")?.Value);
        Assert.AreEqual("false", receiver.Attribute(android + "exported")?.Value);
        Assert.IsNull(receiver.Attribute(android + "directBootAware"));
        Assert.IsFalse(permissions.Contains("android.permission.FOREGROUND_SERVICE_DATA_SYNC"));
        CollectionAssert.Contains(actions, "android.intent.action.BOOT_COMPLETED");
        CollectionAssert.Contains(actions, "android.intent.action.MY_PACKAGE_REPLACED");
        CollectionAssert.DoesNotContain(actions, "android.intent.action.USER_UNLOCKED");
        CollectionAssert.DoesNotContain(actions, "android.intent.action.LOCKED_BOOT_COMPLETED");
    }


    [TestMethod]
    public void UserUnlockedRestorationUsesOnlyTheRunningServicePath()
    {
        var root = GetRepositoryRoot();
        var manifestReceiver = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Background",
            "AndroidBackgroundRestorationReceiver.cs"));
        var unlockReceiver = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Background",
            "AndroidDeferredUnlockReceiver.cs"));
        var service = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "PasswordManagerBackgroundService.cs"));

        Assert.IsFalse(manifestReceiver.Contains("Intent.ActionUserUnlocked", StringComparison.Ordinal));
        StringAssert.Contains(manifestReceiver, "AndroidBackgroundRestorationPolicy");
        StringAssert.Contains(manifestReceiver, "IsUserUnlocked");
        StringAssert.Contains(unlockReceiver, "Intent.ActionUserUnlocked");
        StringAssert.Contains(service, "new AndroidDeferredUnlockReceiver(QueueBackgroundRestoration)");
        StringAssert.Contains(service, "ReceiverFlags.NotExported");
        Assert.IsFalse(unlockReceiver.Contains("StartForegroundService", StringComparison.Ordinal));
        Assert.IsFalse(unlockReceiver.Contains("BackgroundSyncSettingsStore", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Background",
            "AndroidUserUnlockedReceiver.cs")));
    }

    [TestMethod]
    public void ServiceUsesStickyRestorationButRejectsImmediateStartupFailureRestartLoops()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "PasswordManagerBackgroundService.cs"));

        StringAssert.Contains(source, "StartCommandResult.Sticky");
        StringAssert.Contains(source, "StartCommandResult.NotSticky");
        StringAssert.Contains(source, "RequestStop");
    }

    [TestMethod]
    public void UnsafeServiceProcessCannotReenterForegroundOnRestartCommand()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "PasswordManagerBackgroundService.cs"));

        var startMethod = source.IndexOf("OnStartCommand", StringComparison.Ordinal);
        var unsafeCheck = source.IndexOf("runtimeSnapshot.IsRuntimeUnsafe", startMethod, StringComparison.Ordinal);
        var enterForeground = source.IndexOf("EnterForeground(", startMethod, StringComparison.Ordinal);
        Assert.IsTrue(startMethod >= 0);
        Assert.IsTrue(unsafeCheck > startMethod);
        Assert.IsTrue(enterForeground > unsafeCheck);
        StringAssert.Contains(source, "RequestProcessTermination");
        StringAssert.Contains(source, "StartCommandResult.NotSticky");
    }

    [TestMethod]
    public void NotificationTextAndIntentContainNoSensitiveRuntimeData()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "AndroidForegroundServiceController.cs"));

        StringAssert.Contains(source, "Background synchronization is active");
        StringAssert.Contains(source, "Background synchronization is waiting for device unlock");
        StringAssert.Contains(source, "typeof(MainActivity)");
        StringAssert.Contains(source, "PendingIntentFlags.Immutable");
        StringAssert.Contains(source, "Resource.Drawable.ic_stat_password_manager");
        StringAssert.Contains(source, "GetNotificationChannel");
        StringAssert.Contains(source, "CreateNotificationChannel");
        Assert.IsFalse(source.Contains("username", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("password title", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("IPAddress", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsProductionProjectsDoNotReferenceAndroidRuntimeProject()
    {
        var root = GetRepositoryRoot();
        var windowsProjects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("Windows", StringComparison.Ordinal))
            .Where(path => !path.Contains(".Test", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();

        foreach (var project in windowsProjects)
        {
            Assert.IsFalse(
                File.ReadAllText(project).Contains("PasswordManagerLocal.Android.Runtime", StringComparison.Ordinal),
                Path.GetRelativePath(root, project));
        }
    }

    [TestMethod]
    public void AndroidProjectUsesServiceRuntimeProjectWithoutFrontendFallback()
    {
        var root = GetRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "Android",
            "Frontend",
            "PasswordManagerLocal.Android.Frontend.csproj");
        var project = File.ReadAllText(projectPath);
        var androidSources = Directory.EnumerateFiles(
                Path.GetDirectoryName(projectPath)!,
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        StringAssert.Contains(project, "PasswordManagerLocal.Android.Runtime.csproj");
        Assert.IsFalse(androidSources.Any(source =>
            source.Contains("new InProcessFrontendBackendClient", StringComparison.Ordinal)));
        Assert.IsFalse(androidSources.Any(source =>
            source.Contains("StoreBackgroundSyncSettingsClient", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ActivityBackgroundMutationsCarryCurrentAttachmentAuthority()
    {
        var root = GetRepositoryRoot();
        var settingsClient = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "AndroidServiceBackgroundSyncSettingsClient.cs"));
        var service = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "PasswordManagerBackgroundService.cs"));
        var host = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));

        StringAssert.Contains(settingsClient, "_backendClient");
        StringAssert.Contains(settingsClient, "_service.SetBackgroundEnabledAsync(");
        StringAssert.Contains(settingsClient, "_backendClient,");
        StringAssert.Contains(service, "SetBackgroundEnabledFromAttachmentAsync");
        StringAssert.Contains(host, "IsCurrentActiveAttachment(client)");
        StringAssert.Contains(host, "AttachmentGeneration == Interlocked.Read(ref _attachmentGeneration)");
        StringAssert.Contains(host, "CreateAttachmentAuthorityRejectedState");
        var mutationMethod = host.IndexOf("SetBackgroundEnabledCoreAsync", StringComparison.Ordinal);
        var authorityCheck = host.IndexOf(
            "!IsCurrentActiveAttachment(client)",
            mutationMethod,
            StringComparison.Ordinal);
        var settingsRead = host.IndexOf(
            "await LoadSettingsLockedAsync(cancellationToken);",
            mutationMethod,
            StringComparison.Ordinal);
        Assert.IsTrue(mutationMethod >= 0);
        Assert.IsTrue(authorityCheck >= mutationMethod);
        Assert.IsTrue(settingsRead > authorityCheck);
    }

    [TestMethod]
    public void UnsafeInteractiveCleanupClosesBackgroundAndProcessAdmission()
    {
        var host = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));
        var client = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Runtime",
            "AndroidServiceFrontendBackendClient.cs"));
        var handle = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "AndroidActivityServiceAttachmentHandle.cs"));

        StringAssert.Contains(client, "AndroidInteractiveCleanupOutcome.RuntimeUnsafe");
        StringAssert.Contains(host, "FailClosedUnsafeRuntimeLockedAsync");
        StringAssert.Contains(host, "ReleaseBackgroundLeaseLockedAsync");
        StringAssert.Contains(host, "DisposeCompositionLockedAsync");
        StringAssert.Contains(host, "RequestProcessTermination");
        StringAssert.Contains(host, "RequiresProcessRestart = true");
        var restoreMethod = host.IndexOf("RestoreBackgroundStateAsync", StringComparison.Ordinal);
        var unsafeCheck = host.IndexOf("if (_runtimeUnsafe)", restoreMethod, StringComparison.Ordinal);
        var unsafeReturn = host.IndexOf("return _backgroundState;", unsafeCheck, StringComparison.Ordinal);
        var disposedCheck = host.IndexOf("ThrowIfDisposed();", restoreMethod, StringComparison.Ordinal);
        Assert.IsTrue(restoreMethod >= 0);
        Assert.IsTrue(unsafeCheck >= restoreMethod);
        Assert.IsTrue(unsafeReturn > unsafeCheck);
        Assert.IsTrue(disposedCheck > unsafeReturn);
        StringAssert.Contains(handle, "catch");
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));

    private static bool ContainsGeneratedDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");
}
