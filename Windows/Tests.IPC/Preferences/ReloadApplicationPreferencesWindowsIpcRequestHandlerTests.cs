using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class ReloadApplicationPreferencesWindowsIpcRequestHandlerTests
{
    [TestMethod]
    public async Task PayloadFreeRequestRereadsAuthoritativeStore()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var loader = new InMemoryAgentLocalizationResourceLoader();
        loader.SetResource(
            AppLanguage.English,
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("en"));
        loader.SetResource(
            AppLanguage.Hungarian,
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("hu"));
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var tray = new FakeTrayIconController();
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            new FakeApplicationPreferencesFileStampProvider
            {
                Stamp = new ApplicationPreferencesFileStamp(true, 2, DateTime.UnixEpoch)
            },
            new ApplicationPreferencesFileStamp(true, 1, DateTime.UnixEpoch));
        var handler = new ReloadApplicationPreferencesWindowsIpcRequestHandler(coordinator);
        var serializer = new WindowsIpcSerializer();
        var context = new IpcRequestContext(
            new IpcConnectionContext(
                Guid.NewGuid(),
                IpcPeerRole.Ui,
                PeerProcessId: 1,
                PeerWindowsSessionId: 1,
                PeerSessionId: Guid.NewGuid(),
                PeerCapabilities: IpcCapabilities.Control),
            new IpcRequestEnvelope(
                CorrelationId: 7,
                OperationId: IpcOperationId.ReloadApplicationPreferences,
                Payload: null),
            serializer);

        var response = await handler.HandleAsync(context, CancellationToken.None);
        var accepted = serializer.Deserialize(
            response.Result!,
            WindowsIpcJsonContext.Default.RequestAcceptedDto);

        Assert.IsTrue(response.IsSuccess);
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual(1, store.ReadCount);
        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);
    }

    [TestMethod]
    public async Task RequestRejectsAnyLanguageOrThemePayload()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English);
        var localizer = AgentLocalizationTestFactory.CreateEnglish();
        var tray = new FakeTrayIconController();
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            new FakeApplicationPreferencesFileStampProvider(),
            default);
        var handler = new ReloadApplicationPreferencesWindowsIpcRequestHandler(coordinator);
        var context = new IpcRequestContext(
            new IpcConnectionContext(
                Guid.NewGuid(),
                IpcPeerRole.Ui,
                PeerProcessId: 1,
                PeerWindowsSessionId: 1,
                PeerSessionId: Guid.NewGuid(),
                PeerCapabilities: IpcCapabilities.Control),
            new IpcRequestEnvelope(
                CorrelationId: 8,
                OperationId: IpcOperationId.ReloadApplicationPreferences,
                Payload: new byte[] { 1 }),
            new WindowsIpcSerializer());

        await Assert.ThrowsExactlyAsync<IpcPayloadException>(
            () => handler.HandleAsync(context, CancellationToken.None));

        Assert.AreEqual(0, store.ReadCount);
    }
}
