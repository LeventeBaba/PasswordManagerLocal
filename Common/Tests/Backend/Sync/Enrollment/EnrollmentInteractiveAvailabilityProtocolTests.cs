using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Enrollment;

[TestClass]
public sealed class EnrollmentInteractiveAvailabilityProtocolTests
{
    [TestMethod]
    public async Task BackgroundOnly_GetEnrollmentInfoIsRejectedWithStructuredError()
    {
        using var root = CreateBackgroundOnlyProvider();
        var handler = new SyncPeerProtocolHandler(root);

        var reply = await handler.GetDeviceEnrollmentInfoAsync(
            new GetDeviceEnrollmentInfoRequest
            {
                SessionId = "ABCDEFG2",
                SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
            },
            CancellationToken.None);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString(), reply.ErrorCode);
    }

    [TestMethod]
    public async Task BackgroundOnly_EnrollmentStreamIsRejectedBeforeEnumeration()
    {
        using var root = CreateBackgroundOnlyProvider();
        var handler = new SyncPeerProtocolHandler(root);
        var enumerated = false;

        var reply = await handler.CompleteDeviceEnrollmentStreamAsync(
            Chunks(),
            new PeerConnectionContext(),
            DatabaseConstants.CurrentDbVersion,
            CancellationToken.None);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString(), reply.ErrorCode);
        Assert.IsFalse(enumerated);

        async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> Chunks()
        {
            enumerated = true;
            yield return new CompleteDeviceEnrollmentChunk();
            await Task.CompletedTask;
        }
    }

    private static ServiceProvider CreateBackgroundOnlyProvider()
    {
        var profiles = new FakeBackendExecutionProfileProvider();
        profiles.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);
        return new ServiceCollection()
            .AddSingleton<IDeviceEnrollmentAvailability>(profiles)
            .BuildServiceProvider();
    }
}
