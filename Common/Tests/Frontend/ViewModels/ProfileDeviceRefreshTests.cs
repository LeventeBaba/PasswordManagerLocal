using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class ProfileDeviceRefreshTests
{
    [TestMethod]
    public async Task AutomaticLoadsUseCachedQueryAndExplicitRefreshUsesProbeOperation()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecordingDeviceEndpoints>();
        var recording = (RecordingDeviceEndpoints)(object)endpoints;
        await using var uiPreferences = await TestUiPreferencesFactory.CreateAsync();
        using var viewModel = new ProfileViewModel(uiPreferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        viewModel.SetSessionToken(Guid.NewGuid());

        Assert.IsTrue(await viewModel.EnsureDevicesLoadedAsync());
        Assert.AreEqual(1, recording.CachedReadCalls);
        Assert.AreEqual(0, recording.PresenceRefreshCalls);

        Assert.IsTrue(await viewModel.RefreshLoadedDevicesAsync());
        Assert.AreEqual(2, recording.CachedReadCalls);
        Assert.AreEqual(0, recording.PresenceRefreshCalls);

        await viewModel.RefreshDevicesOnlyAsync();
        Assert.AreEqual(2, recording.CachedReadCalls);
        Assert.AreEqual(1, recording.PresenceRefreshCalls);
    }

    public class RecordingDeviceEndpoints : DispatchProxy
    {
        public int CachedReadCalls { get; private set; }
        public int PresenceRefreshCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IEndpoints.GetUserDevicesAsync):
                    CachedReadCalls++;
                    return Task.FromResult<IReadOnlyList<UserDeviceInfoResponse>>([]);
                case nameof(IEndpoints.RefreshUserDevicePresenceAsync):
                    PresenceRefreshCalls++;
                    return Task.FromResult<IReadOnlyList<UserDeviceInfoResponse>>([]);
                default:
                    throw new NotSupportedException($"Unexpected endpoint call: {targetMethod?.Name}.");
            }
        }
    }
}
