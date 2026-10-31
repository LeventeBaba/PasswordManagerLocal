using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class AndroidLifecycleSecurityArchitectureTests
{
    [TestMethod]
    public void EveryEndpointInvocationPassesThroughCurrentAttachmentAuthority()
    {
        var root = GetRepositoryRoot();
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
        var host = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "Runtime",
            "AndroidRuntimeServiceHost.cs"));

        var contractMethods = typeof(IEndpoints).GetMethods()
            .Select(method => method.Name)
            .ToArray();
        var wrapperMethods = Regex.Matches(
                wrapper,
                @"(?m)^\s*public\s+[^\r\n;]+?\s+(?<name>\w+Async)\s*\(")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        var forwardedMethods = Regex.Matches(
                wrapper,
                @"(?m)^\s*public\s+[^\r\n;]+?\s+(?<name>\w+Async)\s*\([^)]*\)\s*=>\s*InvokeAsync\(\(\)\s*=>\s*_inner\.(?<forwarded>\w+Async)\s*\(")
            .Select(match => (
                Method: match.Groups["name"].Value,
                Forwarded: match.Groups["forwarded"].Value))
            .ToArray();

        AssertMethodSetEquals(contractMethods, wrapperMethods, "Public Android endpoint wrapper methods");
        AssertMethodSetEquals(
            contractMethods,
            forwardedMethods.Select(item => item.Method),
            "Android endpoint methods routed through the authorization wrapper");
        AssertMethodSetEquals(
            contractMethods,
            forwardedMethods.Select(item => item.Forwarded),
            "Android endpoint methods forwarded to the current backend session");
        Assert.IsTrue(forwardedMethods.All(item => item.Method == item.Forwarded),
            "Every wrapper method must invoke the matching IEndpoints member.");

        var invocationGuards = Regex.Matches(
                wrapper,
                @"(?m)^\s*private\s+Task(?:<T>)?\s+InvokeAsync(?:<T>)?\s*\([^)]*\)\s*\{(?<body>[^}]*)\}")
            .Select(match => match.Groups["body"].Value)
            .ToArray();
        Assert.IsTrue(invocationGuards.Length > 0, "The endpoint wrapper must centralize endpoint invocation.");
        foreach (var body in invocationGuards)
        {
            var authorityCheck = body.IndexOf("_owner.EnsureEndpointAuthority(_client);", StringComparison.Ordinal);
            var operationCall = body.IndexOf("return operation();", StringComparison.Ordinal);
            Assert.IsTrue(authorityCheck >= 0 && operationCall > authorityCheck,
                "Each invocation helper must validate current attachment authority before calling the endpoint.");
        }

        var getEndpoints = Regex.Match(
            client,
            @"(?s)public async Task<IEndpoints>\s+GetEndpointsAsync\([^)]*\)\s*\{(?<body>.*?)\n    \}");
        Assert.IsTrue(getEndpoints.Success, "The frontend endpoint attachment factory must remain discoverable.");
        var getEndpointsBody = getEndpoints.Groups["body"].Value;
        var authorityIndex = getEndpointsBody.IndexOf("_owner.EnsureEndpointAuthority(this);", StringComparison.Ordinal);
        var wrapperIndex = getEndpointsBody.IndexOf(
            "return new AndroidAttachmentAuthorizedEndpoints(_owner, this, endpoints);",
            StringComparison.Ordinal);
        Assert.IsTrue(authorityIndex >= 0 && wrapperIndex > authorityIndex,
            "GetEndpointsAsync must authorize the current attachment before returning the guarded endpoint proxy.");
        StringAssert.Contains(getEndpointsBody, "_interactiveSession?.Endpoints");

        var authorityCheckMethod = Regex.Match(
            host,
            @"(?s)internal void EnsureEndpointAuthority\(AndroidServiceFrontendBackendClient client\)\s*\{(?<body>[^}]*)\}");
        Assert.IsTrue(authorityCheckMethod.Success);
        StringAssert.Contains(authorityCheckMethod.Groups["body"].Value, "!IsCurrentActiveAttachment(client)");

        var activeAttachment = Regex.Match(
            host,
            @"private bool IsCurrentActiveAttachment\([^)]*\)\s*=>\s*(?<body>[^;]+);");
        Assert.IsTrue(activeAttachment.Success);
        Assert.AreEqual(
            "!_runtimeUnsafe && IsCurrentInteractiveClient(client) && client.HasMutationAuthority",
            Regex.Replace(activeAttachment.Groups["body"].Value, @"\s+", " ").Trim());

        var currentClient = Regex.Match(
            host,
            @"private bool IsCurrentInteractiveClient\([^)]*\)\s*=>\s*(?<body>[^;]+);");
        Assert.IsTrue(currentClient.Success);
        Assert.AreEqual(
            "ReferenceEquals(Volatile.Read(ref _interactiveClient), client) && client.AttachmentGeneration == Interlocked.Read(ref _attachmentGeneration)",
            Regex.Replace(currentClient.Groups["body"].Value, @"\s+", " ").Trim());

        var mutationAuthority = Regex.Match(
            client,
            @"internal bool HasMutationAuthority\s*=>\s*(?<body>[^;]+);");
        Assert.IsTrue(mutationAuthority.Success);
        Assert.AreEqual(
            "AttachmentState == AndroidInteractiveAttachmentState.Active",
            Regex.Replace(mutationAuthority.Groups["body"].Value, @"\s+", " ").Trim());
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
    public void AndroidVerificationToolsAndReportsArePermanentRepositoryInputs()
    {
        var root = GetRepositoryRoot();
        foreach (var relativePath in new[]
        {
            Path.Combine("Tools", "Android", "VerifyAndroidManifestAndResources.ps1"),
            Path.Combine("Tools", "Android", "VerifyAndroidPackage.ps1"),
            Path.Combine("Tools", "Android", "InvokeAndroidIntegrationChecks.ps1")
        })
        {
            Assert.IsTrue(File.Exists(Path.Combine(root, relativePath)), relativePath);
        }

        var documentationVerifierPath = Path.Combine(root, "Tools", "VerifyDocumentationLayout.ps1");
        Assert.IsTrue(File.Exists(documentationVerifierPath), documentationVerifierPath);
        var documentationVerifier = File.ReadAllText(documentationVerifierPath);

        foreach (var reportName in new[]
        {
            "ANDROID_INTEGRATION_PLATFORM_HARDENING_STATIC_VERIFICATION.md",
            "ANDROID_INTEGRATION_IMPLEMENTATION_CHECKS.md",
            "ANDROID_INTEGRATION_MODIFIED_FILES.md",
            "ANDROID_INTEGRATION_AUTOMATED_TEST_MATRIX.md",
            "ANDROID_INTEGRATION_EMULATOR_DEVICE_TEST_MATRIX.md",
            "ANDROID_INTEGRATION_SECURITY_REVIEW.md",
            "ANDROID_INTEGRATION_ARCHITECTURE_AND_ACCEPTANCE.md"
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
            "InvokeAndroidIntegrationChecks.ps1"));

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
    public void EndpointAndMutationCompletenessGuardsUseDerivedSetsAndCoverSelfDiagnostics()
    {
        var root = GetRepositoryRoot();
        var paritySource = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "Tests.EndpointRpc",
            "Mapping",
            "EndpointOperationParityTests.cs"));
        var proxySource = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "Tests.EndpointRpc",
            "Client",
            "NamedPipeEndpointsProxyMappingTests.cs"));

        StringAssert.Contains(paritySource, "typeof(IEndpoints).GetMethods()");
        StringAssert.Contains(paritySource, "EndpointOperationTestAssertions.AreEquivalent");
        StringAssert.Contains(paritySource, "Enum.GetValues<EndpointOperationId>()");
        StringAssert.Contains(paritySource, "descriptor.CommitModel != EndpointMutationCommitModel.NoDurableMutation");
        StringAssert.Contains(paritySource, "descriptor.MutatesState");
        StringAssert.Contains(proxySource, "EndpointOperationManifest.All");
        StringAssert.Contains(proxySource, "nameof(IEndpoints.RunSelfDiagnosticsAndRepairAsync)");
        StringAssert.Contains(proxySource, "EndpointOperationTestAssertions.AreEquivalent");
    }

    private static void AssertMethodSetEquals(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string description)
    {
        var expectedItems = expected.ToArray();
        var actualItems = actual.ToArray();
        var expectedSet = expectedItems.ToHashSet(StringComparer.Ordinal);
        var actualSet = actualItems.ToHashSet(StringComparer.Ordinal);
        var expectedDuplicates = expectedItems.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        var actualDuplicates = actualItems.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).ToArray();
        var unexpected = actualSet.Except(expectedSet, StringComparer.Ordinal).ToArray();

        Assert.IsTrue(expectedDuplicates.Length == 0,
            $"{description} expected set has duplicates: {string.Join(", ", expectedDuplicates)}.");
        Assert.IsTrue(actualDuplicates.Length == 0,
            $"{description} has duplicate entries: {string.Join(", ", actualDuplicates)}.");
        Assert.IsTrue(missing.Length == 0 && unexpected.Length == 0,
            $"{description} differs. Missing: [{string.Join(", ", missing)}]. " +
            $"Unexpected: [{string.Join(", ", unexpected)}].");
    }

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
