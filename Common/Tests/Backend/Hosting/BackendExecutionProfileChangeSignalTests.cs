using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class BackendExecutionProfileChangeSignalTests
{
    [TestMethod]
    public async Task EffectiveProfileChange_WakesCurrentDelay()
    {
        var provider = CreateProvider(TimeSpan.FromSeconds(60), isInteractive: false);
        using var signal = new BackendExecutionProfileChangeSignal(provider);
        var version = signal.Version;
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(1), version, CancellationToken.None);

        provider.SetProfile(CreateProfile(TimeSpan.FromSeconds(15)), isInteractive: true);

        Assert.IsTrue(await wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task RepeatedSameProfile_DoesNotWakeCurrentDelay()
    {
        var profile = CreateProfile(TimeSpan.FromSeconds(60));
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(profile, isInteractive: false);
        using var signal = new BackendExecutionProfileChangeSignal(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var version = signal.Version;

        provider.SetProfile(profile, isInteractive: false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await signal.WaitAsync(TimeSpan.FromMinutes(1), version, cancellation.Token));
    }

    [TestMethod]
    public async Task HostedServiceCancellation_CancelsDelayInsteadOfReportingProfileWake()
    {
        var provider = CreateProvider(TimeSpan.FromSeconds(60), isInteractive: false);
        using var signal = new BackendExecutionProfileChangeSignal(provider);
        using var cancellation = new CancellationTokenSource();
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(1), signal.Version, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await wait);
    }

    private static FakeBackendExecutionProfileProvider CreateProvider(TimeSpan interval, bool isInteractive)
    {
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(CreateProfile(interval), isInteractive);
        return provider;
    }

    private static BackendExecutionProfile CreateProfile(TimeSpan interval) =>
        new(interval, interval, TimeSpan.FromSeconds(35));
}
