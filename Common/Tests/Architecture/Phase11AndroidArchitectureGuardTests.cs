using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase11AndroidArchitectureGuardTests
{
    [TestMethod]
    public void EveryEndpointInvocationPassesThroughCurrentAttachmentAuthority()
    {
        var root = GetRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(
            root,
            "Common",
            "Contracts",
            "Endpoints",
            "IEndpoints.cs"));
        var wrapper = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidAttachmentAuthorizedEndpoints.cs"));
        var client = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidServiceFrontendBackendClient.cs"));

        var contractMethods = ExtractAsyncMethodNames(contract);
        var forwardedMethods = Regex.Matches(wrapper, @"_inner\.(?<name>\w+Async)\(")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.AreEqual(40, contractMethods.Length);
        CollectionAssert.AreEquivalent(contractMethods, forwardedMethods);
        Assert.AreEqual(40, forwardedMethods.Distinct(StringComparer.Ordinal).Count());
        StringAssert.Contains(wrapper, "_owner.EnsureEndpointAuthority(_client);");
        StringAssert.Contains(client, "return new AndroidAttachmentAuthorizedEndpoints");
        Assert.IsFalse(client.Contains("return endpoints;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeOwnerAndRestorationComponentsRemainNarrowAndNonExported()
    {
        var root = GetRepositoryRoot();
        var manifestPath = Path.Combine(
            root,
            "Android",
            "Frontend",
            "Properties",
            "AndroidManifest.xml");
        var document = XDocument.Load(manifestPath);
        XNamespace android = "http://schemas.android.com/apk/res/android";
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

        Assert.AreEqual("false", service.Attribute(android + "exported")?.Value);
        Assert.AreEqual("connectedDevice", service.Attribute(android + "foregroundServiceType")?.Value);
        Assert.AreEqual("false", receiver.Attribute(android + "exported")?.Value);
        Assert.IsNull(receiver.Attribute(android + "directBootAware"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "android.intent.action.BOOT_COMPLETED",
                "android.intent.action.MY_PACKAGE_REPLACED"
            },
            actions);
        CollectionAssert.DoesNotContain(actions, "android.intent.action.LOCKED_BOOT_COMPLETED");

        var activitySource = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "MainActivity.cs"));
        StringAssert.Contains(activitySource, "MainLauncher = true");
        StringAssert.Contains(activitySource, "Exported = true");
    }

    [TestMethod]
    public void UserUnlockCannotStartTheServiceFromAManifestReceiver()
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
        StringAssert.Contains(unlockReceiver, "Intent.ActionUserUnlocked");
        StringAssert.Contains(unlockReceiver, "_queueRestoration();");
        Assert.IsFalse(unlockReceiver.Contains("StartForegroundService", StringComparison.Ordinal));
        Assert.IsFalse(unlockReceiver.Contains("BackgroundSyncSettingsStore", StringComparison.Ordinal));
        StringAssert.Contains(service, "new AndroidDeferredUnlockReceiver(QueueBackgroundRestoration)");
        StringAssert.Contains(service, "ReceiverFlags.NotExported");
        StringAssert.Contains(service, "UnregisterDeferredUnlockReceiver");
    }

    [TestMethod]
    public void ForegroundNotificationUsesExplicitImmutableIntentAndGenericContent()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "AndroidForegroundServiceController.cs"));

        StringAssert.Contains(source, "new Intent(_service, typeof(MainActivity))");
        StringAssert.Contains(source, "PendingIntentFlags.Immutable");
        StringAssert.Contains(source, "Resource.Drawable.ic_stat_password_manager");
        Assert.IsFalse(source.Contains("PutExtra(", StringComparison.Ordinal));
        foreach (var forbidden in new[]
        {
            "masterPassword",
            "enrollment code",
            "auth token",
            "device key",
            "sync payload",
            "stack trace"
        })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
    }

    [TestMethod]
    public void AndroidLifecycleCallbacksNeverSynchronouslyWaitForRuntimeCleanup()
    {
        var root = GetRepositoryRoot();
        var lifecycleSources = new[]
        {
            Path.Combine(
                root,
                "Android",
                "Frontend",
                "MainActivity.cs"),
            Path.Combine(
                root,
                "Android",
                "Frontend",
                "Runtime",
                "PasswordManagerBackgroundService.cs")
        };

        foreach (var sourcePath in lifecycleSources)
        {
            var source = File.ReadAllText(sourcePath);
            Assert.IsFalse(
                source.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal),
                Path.GetRelativePath(root, sourcePath));
            Assert.IsFalse(
                Regex.IsMatch(source, @"\.Wait\s*\("),
                Path.GetRelativePath(root, sourcePath));
        }

        var activity = File.ReadAllText(lifecycleSources[0]);
        var service = File.ReadAllText(lifecycleSources[1]);
        StringAssert.Contains(activity, "DisposeSessionAsync");
        StringAssert.Contains(activity, "_ = DisposeSessionAsync(session);");
        StringAssert.Contains(service, "Task.Run(() => DisposeRuntimeHostAfterServiceDestructionAsync");
        StringAssert.Contains(service, "DisposeRuntimeResourcesAsync");
    }


    [TestMethod]
    public void AndroidLifecycleProductionCodeDoesNotLogOrSerializeSensitiveState()
    {
        var root = GetRepositoryRoot();
        var lifecycleRoots = new[]
        {
            Path.Combine(root, "Android", "Runtime"),
            Path.Combine(root, "Android", "Frontend", "Runtime"),
            Path.Combine(root, "Android", "Frontend", "Background")
        };
        var sources = lifecycleRoots
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path => !ContainsGeneratedDirectory(path))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();

        foreach (var source in sources)
        {
            foreach (var forbiddenLoggingApi in new[]
            {
                "Android.Util.Log.",
                "Console.Write",
                "Debug.Write",
                "Trace.Write"
            })
            {
                Assert.IsFalse(
                    source.Text.Contains(forbiddenLoggingApi, StringComparison.Ordinal),
                    Path.GetRelativePath(root, source.Path));
            }
        }

        foreach (var relativePath in new[]
        {
            "Android/Frontend/Runtime/PasswordManagerBackgroundService.cs",
            "Android/Frontend/Runtime/AndroidForegroundServiceController.cs",
            "Android/Frontend/Background/AndroidBackgroundRestorationReceiver.cs",
            "Android/Frontend/Background/AndroidDeferredUnlockReceiver.cs"
        })
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.IsFalse(source.Contains("PutExtra(", StringComparison.Ordinal), relativePath);
        }
    }

    [TestMethod]
    public void UnsafeProcessAdmissionPrecedesEveryRuntimeCreationPath()
    {
        var host = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "PasswordManagerBackgroundService.cs"));

        StringAssert.Contains(host, "private volatile bool _runtimeUnsafe;");
        StringAssert.Contains(host, "ThrowIfUnavailable();");
        StringAssert.Contains(host, "if (_runtimeUnsafe)");
        StringAssert.Contains(host, "RequiresProcessRestart = true");
        StringAssert.Contains(host, "RequestProcessTermination");
        var unsafeCheck = service.IndexOf("runtimeSnapshot.IsRuntimeUnsafe", StringComparison.Ordinal);
        var foregroundEntry = service.IndexOf("EnterForeground(", unsafeCheck, StringComparison.Ordinal);
        Assert.IsTrue(unsafeCheck >= 0);
        Assert.IsTrue(foregroundEntry > unsafeCheck);
    }

    [TestMethod]
    public void AndroidSourceContainsNoActivityRuntimeFallbackOrStaticServiceLocator()
    {
        var root = GetRepositoryRoot();
        var androidRoot = Path.Combine(root, "Android", "Frontend");
        var sources = Directory.EnumerateFiles(androidRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Select(File.ReadAllText)
            .ToArray();
        var activity = File.ReadAllText(Path.Combine(androidRoot, "MainActivity.cs"));
        var application = File.ReadAllText(Path.Combine(androidRoot, "PasswordManagerLocalApplication.cs"));

        Assert.IsFalse(activity.Contains("AndroidBackendRuntimeFactory", StringComparison.Ordinal));
        Assert.IsFalse(application.Contains("AndroidBackendRuntimeFactory", StringComparison.Ordinal));
        Assert.IsFalse(sources.Any(source => source.Contains("static PasswordManagerBackgroundService", StringComparison.Ordinal)));
        Assert.IsFalse(sources.Any(source => source.Contains("static AndroidRuntimeServiceHost", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Phase11VerificationToolsAndReportsArePermanentRepositoryInputs()
    {
        var root = GetRepositoryRoot();
        foreach (var relativePath in new[]
        {
            Path.Combine("Tools", "Android", "VerifyAndroidManifestAndResources.ps1"),
            Path.Combine("Tools", "Android", "VerifyAndroidPackage.ps1"),
            Path.Combine("Tools", "Android", "InvokePhase11AndroidChecks.ps1")
        })
        {
            Assert.IsTrue(File.Exists(Path.Combine(root, relativePath)), relativePath);
        }

        var documentationVerifierPath = Path.Combine(root, "Tools", "VerifyDocumentationLayout.ps1");
        Assert.IsTrue(File.Exists(documentationVerifierPath), documentationVerifierPath);
        var documentationVerifier = File.ReadAllText(documentationVerifierPath);

        foreach (var reportName in new[]
        {
            "PHASE11_ANDROID_INTEGRATION_PLATFORM_HARDENING_STATIC_VERIFICATION.md",
            "PHASE11_IMPLEMENTATION_CHECKS.md",
            "PHASE11_MODIFIED_FILES.md",
            "PHASE11_ANDROID_AUTOMATED_TEST_MATRIX.md",
            "PHASE11_ANDROID_EMULATOR_DEVICE_TEST_MATRIX.md",
            "PHASE11_ANDROID_SECURITY_REVIEW.md",
            "PHASE11_FINAL_ARCHITECTURE_AND_ACCEPTANCE.md"
        })
        {
            Assert.IsTrue(
                documentationVerifier.Contains($"'{reportName}'", StringComparison.Ordinal),
                reportName);
        }
    }

    [TestMethod]
    public void AndroidPackageVerifierChecksLauncherAndRuntimeOwnerUniqueness()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Tools",
            "Android",
            "VerifyAndroidPackage.ps1"));

        StringAssert.Contains(source, "$activityContext = Get-ManifestContext $manifestText 'MainActivity' 24");
        StringAssert.Contains(source, "android.intent.action.MAIN");
        StringAssert.Contains(source, "android.intent.category.LAUNCHER");
        StringAssert.Contains(source, "$directRuntimeOwnerEntries.Count -le 1");
        StringAssert.Contains(source, "PasswordManagerLocal.Android.Runtime.dll");
    }

    [TestMethod]
    public void AndroidDiagnosticScriptUsesNoCoordinateAutomationOrProductionDataAccess()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Tools",
            "Android",
            "InvokePhase11AndroidChecks.ps1"));

        StringAssert.Contains(source, "[Parameter(Mandatory)] [string]$PackageName");
        StringAssert.Contains(source, "[Parameter(Mandatory)] [string]$ActivityName");
        StringAssert.Contains(source, "TimeoutSeconds");
        Assert.IsFalse(source.Contains("input tap", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("input swipe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("run-as", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("/data/data", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void WindowsOwnershipSourcesRemainAndroidIndependent()
    {
        var root = GetRepositoryRoot();
        var windowsProductionSources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains("Windows", StringComparison.Ordinal))
            .Where(path => !path.Contains(".Test", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !ContainsGeneratedDirectory(path));

        foreach (var path in windowsProductionSources)
        {
            Assert.IsFalse(
                File.ReadAllText(path).Contains("PasswordManagerLocal.Android.Runtime", StringComparison.Ordinal),
                Path.GetRelativePath(root, path));
        }
    }


    [TestMethod]
    public void RepeatedServiceStartsReuseForegroundStateAndOneInFlightRestoration()
    {
        var root = GetRepositoryRoot();
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

        var foregroundReuse = service.IndexOf("if (runtimeSnapshot.IsForeground)", StringComparison.Ordinal);
        var foregroundEntry = service.IndexOf("EnterForeground(", StringComparison.Ordinal);
        Assert.IsTrue(foregroundReuse >= 0);
        Assert.IsTrue(foregroundEntry > foregroundReuse);
        StringAssert.Contains(service, "_restorationTask is { IsCompleted: false }");
        var controller = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "AndroidForegroundServiceController.cs"));
        StringAssert.Contains(controller, "_foregroundNotificationState == state ||");
        StringAssert.Contains(
            controller,
            "_foregroundNotificationState == AndroidForegroundNotificationState.BackgroundSynchronizationActive");
        StringAssert.Contains(host, "_backgroundLease is not null && _backgroundState.IsForegroundActive");
        StringAssert.Contains(host, "_backgroundState = CreateOperationalState(true);");
    }

    [TestMethod]
    public void ActivityInitializationTimeoutCoversBindingAndRuntimeAttachment()
    {
        var connector = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Frontend",
            "Runtime",
            "AndroidRuntimeServiceConnector.cs"));

        StringAssert.Contains(connector, "connection.WaitForServiceAsync(timeoutSource.Token)");
        StringAssert.Contains(connector, "service.AttachInteractiveClientAsync(timeoutSource.Token)");
        StringAssert.Contains(connector, "catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)");
        StringAssert.Contains(connector, "did not finish activity attachment in time");
        Assert.IsFalse(connector.Contains(
            "service.AttachInteractiveClientAsync(cancellationToken)",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void SecureStorageGatePrecedesEveryRestorationSettingRead()
    {
        var host = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));
        var restore = host.IndexOf("RestoreBackgroundStateAsync", StringComparison.Ordinal);
        var availability = host.IndexOf("!_secureStorageAvailability.IsAvailable", restore, StringComparison.Ordinal);
        var settingRead = host.IndexOf("LoadSettingsLockedAsync", restore, StringComparison.Ordinal);
        var attach = host.IndexOf("AttachInteractiveClientAsync", StringComparison.Ordinal);
        var attachAvailability = host.IndexOf("!_secureStorageAvailability.IsAvailable", attach, StringComparison.Ordinal);
        var attachSettingRead = host.IndexOf("LoadSettingsLockedAsync", attach, StringComparison.Ordinal);
        var settingMutation = host.IndexOf("SetBackgroundEnabledCoreAsync", StringComparison.Ordinal);
        var mutationAvailability = host.IndexOf("!_secureStorageAvailability.IsAvailable", settingMutation, StringComparison.Ordinal);
        var mutationSettingRead = host.IndexOf("LoadSettingsLockedAsync", settingMutation, StringComparison.Ordinal);

        Assert.IsTrue(restore >= 0);
        Assert.IsTrue(availability > restore);
        Assert.IsTrue(settingRead > availability);
        Assert.IsTrue(attachAvailability > attach);
        Assert.IsTrue(attachSettingRead > attachAvailability);
        Assert.IsTrue(mutationAvailability > settingMutation);
        Assert.IsTrue(mutationSettingRead > mutationAvailability);
        StringAssert.Contains(host, "EnterDeferredUntilUnlockLockedAsync");
        StringAssert.Contains(host, "CreateSecureStorageUnavailableState");

        var root = GetRepositoryRoot();
        var availabilitySource = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "AndroidSecureStorageAvailability.cs"));
        var receiver = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Background",
            "AndroidBackgroundRestorationReceiver.cs"));
        StringAssert.Contains(availabilitySource, "userManager?.IsUserUnlocked == true");
        StringAssert.Contains(receiver, "userManager?.IsUserUnlocked == true");
    }

    [TestMethod]
    public void DatabaseResetSuspendsEndpointAuthorityUntilSafeCompletion()
    {
        var root = GetRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));
        var client = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidServiceFrontendBackendClient.cs"));

        var reset = host.IndexOf("ResetDatabaseAsync", StringComparison.Ordinal);
        var suspend = host.IndexOf("SuspendAuthorityForResetFromHost", reset, StringComparison.Ordinal);
        var close = host.IndexOf("CloseFromHostAsync", reset, StringComparison.Ordinal);
        var resume = host.IndexOf("ResumeAuthorityAfterResetFromHost", reset, StringComparison.Ordinal);
        Assert.IsTrue(reset >= 0);
        Assert.IsTrue(suspend > reset);
        Assert.IsTrue(close > suspend);
        Assert.IsTrue(resume > close);
        var restoreBackground = host.IndexOf("EnsureBackgroundRuntimeLockedAsync(cancellationToken)", close, StringComparison.Ordinal);
        Assert.IsTrue(restoreBackground > close);
        StringAssert.Contains(client, "AndroidInteractiveAttachmentState.Resetting");
        StringAssert.Contains(client, "temporarily unavailable during database reset");
    }

    [TestMethod]
    public void DeferredAndroidClientReplaysReadinessAndFrontendConfirmsConnectedSnapshot()
    {
        var root = GetRepositoryRoot();
        var deferredClient = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "Runtime",
            "AndroidDeferredServiceFrontendBackendClient.cs"));
        var mainViewModel = File.ReadAllText(Path.Combine(
            root,
            "Common",
            "Frontend",
            "ViewModels",
            "MainViewModel.cs"));

        var subscription = deferredClient.IndexOf(
            "inner.StateChanged += HandleInnerStateChanged;",
            StringComparison.Ordinal);
        var replay = deferredClient.IndexOf(
            "ApplyInnerSnapshot(inner.Snapshot);",
            StringComparison.Ordinal);

        Assert.IsTrue(subscription >= 0);
        Assert.IsTrue(replay > subscription);
        StringAssert.Contains(deferredClient, "private readonly object _snapshotGate = new();");
        StringAssert.Contains(deferredClient, "sender is IBackendRuntimeClient runtimeClient");
        StringAssert.Contains(deferredClient, "PublishStateChanged(new BackendRuntimeStateChangedEventArgs(previous, current));");
        StringAssert.Contains(mainViewModel, "var snapshot = _backendClient.Snapshot;");
        StringAssert.Contains(mainViewModel, "ApplyBackendAvailability(snapshot.IsReady);");
    }

    [TestMethod]
    public void NetworkingResourcesRemainRuntimeOwnedAndAbsentFromMainActivity()
    {
        var root = GetRepositoryRoot();
        var activity = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Frontend",
            "MainActivity.cs"));
        var networkLease = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Backend",
            "AndroidLocalDiscoveryNetworkLease.cs"));

        foreach (var forbidden in new[] { "MulticastLock", "WifiLock", "TcpListener", "RegisterNetworkCallback" })
            Assert.IsFalse(activity.Contains(forbidden, StringComparison.Ordinal), forbidden);

        StringAssert.Contains(networkLease, "CreateMulticastLock");
        StringAssert.Contains(networkLease, "Release");
    }

    [TestMethod]
    public void EndpointAndMutationCompletenessGuardsRemainAtFortyAndThirtyTwo()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Tests.EndpointRpc",
            "Mapping",
            "EndpointOperationParityTests.cs"));

        StringAssert.Contains(source, "Assert.AreEqual(40, EndpointOperationManifest.All.Count);");
        StringAssert.Contains(source, "Assert.HasCount(40, methods);");
        StringAssert.Contains(source, "Assert.HasCount(32, mutations);");
        StringAssert.Contains(source, "operationIds.Distinct()");
    }

    private static string[] ExtractAsyncMethodNames(string source) =>
        Regex.Matches(source, @"\b(?<name>\w+Async)\s*\(")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var startPaths = new[]
        {
            Path.GetDirectoryName(sourcePath),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var startPath in startPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            for (var directory = new DirectoryInfo(startPath!); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PasswordManagerLocal.sln")) &&
                    File.Exists(Path.Combine(
                        directory.FullName,
                        "Common",
                        "Tests",
                        "PasswordManagerLocal.Common.Tests.csproj")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from source path '{sourcePath}', " +
            $"base directory '{AppContext.BaseDirectory}', or current directory '{Directory.GetCurrentDirectory()}'.");
    }

    private static bool ContainsGeneratedDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");
}
