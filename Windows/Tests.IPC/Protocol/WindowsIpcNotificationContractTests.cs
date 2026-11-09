using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Protocol;

[TestClass]
public sealed class WindowsIpcNotificationContractTests
{
    [TestMethod]
    public void NotificationCapabilityIsRecognizedByHandshakeAndClientOptions()
    {
        var capabilities = IpcCapabilities.Control | IpcCapabilities.ChangeNotifications;
        var request = new IpcHandshakeRequest(
            IpcPeerRole.Ui,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            capabilities);

        new WindowsIpcContractValidator().Validate(request);
        var options = new WindowsIpcClientOptions(
            IpcPeerRole.Ui,
            IpcPeerRole.Agent,
            capabilities,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            requiredServerCapabilities: IpcCapabilities.ChangeNotifications);

        Assert.AreEqual(capabilities, options.Capabilities);
        Assert.AreEqual(IpcCapabilities.ChangeNotifications, options.RequiredServerCapabilities);

        var serverOptions = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            new[] { IpcPeerRole.Ui },
            capabilities,
            changeNotificationQueueCapacity: 8);
        Assert.AreEqual(8, serverOptions.ChangeNotificationQueueCapacity);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            new[] { IpcPeerRole.Ui },
            capabilities,
            changeNotificationQueueCapacity: 0));
    }

    [TestMethod]
    public void NotificationContractIsMetadataOnlyAndRoundTripsThroughSourceGeneratedJson()
    {
        var notification = new IpcChangeNotificationDto(
            Guid.NewGuid(),
            17,
            FrontendChangeScope.Passwords | FrontendChangeScope.Devices,
            DateTimeOffset.UtcNow,
            RequiresAuthoritativeRefresh: false);
        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();

        validator.Validate(notification);
        var bytes = serializer.Serialize(
            notification,
            WindowsIpcJsonContext.Default.IpcChangeNotificationDto);
        var restored = serializer.Deserialize(
            bytes,
            WindowsIpcJsonContext.Default.IpcChangeNotificationDto);

        Assert.AreEqual(notification, restored);
        Assert.IsFalse(typeof(IpcChangeNotificationDto).GetProperties()
            .Any(property => property.Name.Contains("User", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InvalidNotificationWatermarksScopesAndUserFiltersAreRejected()
    {
        var validator = new WindowsIpcContractValidator();
        var runtimeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new IpcChangeNotificationDto(Guid.Empty, 1, FrontendChangeScope.Passwords, timestamp, false)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new IpcChangeNotificationDto(runtimeId, 0, FrontendChangeScope.Passwords, timestamp, false)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new IpcChangeNotificationDto(runtimeId, 1, (FrontendChangeScope)int.MaxValue, timestamp, false)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new IpcChangeNotificationDto(runtimeId, 1, FrontendChangeScope.Passwords, timestamp, true)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new IpcChangeNotificationDto(runtimeId, 1, FrontendChangeScope.Devices, timestamp.ToOffset(TimeSpan.FromHours(1)), false)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new SetChangeNotificationUserRequestDto(Guid.Empty, Enabled: true)));
        Assert.ThrowsExactly<IpcPayloadException>(() => validator.Validate(
            new SetChangeNotificationUserRequestDto(Guid.NewGuid(), Enabled: false)));

        validator.Validate(new IpcChangeNotificationDto(
            runtimeId,
            0,
            FrontendChangeScope.Everything,
            timestamp,
            RequiresAuthoritativeRefresh: true));
        validator.Validate(new SetChangeNotificationUserRequestDto(null, Enabled: true));
        validator.Validate(new SetChangeNotificationUserRequestDto(null, Enabled: false));
    }
}
