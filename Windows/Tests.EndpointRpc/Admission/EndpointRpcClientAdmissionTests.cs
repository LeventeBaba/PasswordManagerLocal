using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Admission;

[TestClass]
public sealed class EndpointRpcClientAdmissionTests
{
    [TestMethod]
    public void ClientOptionsAcceptMinimumDefaultAndMaximumOnly()
    {
        Assert.AreEqual(1, new EndpointRpcClientOptions(1).MaximumConcurrentOperations);
        Assert.AreEqual(
            EndpointRpcClientOptions.DefaultMaximumConcurrentOperations,
            new EndpointRpcClientOptions().MaximumConcurrentOperations);
        Assert.AreEqual(
            EndpointRpcClientOptions.MaximumConfigurableConcurrentOperations,
            new EndpointRpcClientOptions(
                EndpointRpcClientOptions.MaximumConfigurableConcurrentOperations)
                .MaximumConcurrentOperations);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new EndpointRpcClientOptions(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new EndpointRpcClientOptions(
                EndpointRpcClientOptions.MaximumConfigurableConcurrentOperations + 1));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CapacityIsAcquiredBeforeRequestCreationAndSerialization()
    {
        var transport = new BlockingEndpointRpcTransport();
        var serializationCount = 0;
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(LogoutEndpointRequest))
                Interlocked.Increment(ref serializationCount);
        });
        await using var proxy = CreateProxy(transport, serializer, maximumConcurrentOperations: 1);

        var first = proxy.LogoutAsync(EndpointRpcTestData.Token);
        await transport.FirstSendStarted;
        var second = proxy.LogoutAsync(EndpointRpcTestData.Token);
        await Task.Yield();

        Assert.AreEqual(1, Volatile.Read(ref serializationCount));
        Assert.AreEqual(1, transport.SendCount);
        Assert.IsFalse(second.IsCompleted);

        transport.Release();
        await first;
        await second;
        Assert.AreEqual(2, Volatile.Read(ref serializationCount));
        Assert.AreEqual(2, transport.SendCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OversizedRawInputIsRejectedBeforeAdmissionOrSerialization()
    {
        var transport = new BlockingEndpointRpcTransport();
        var serializationCount = 0;
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(SetLocalDeviceNameEndpointRequest))
                Interlocked.Increment(ref serializationCount);
        });
        await using var proxy = CreateProxy(transport, serializer, maximumConcurrentOperations: 1);
        var admitted = proxy.LogoutAsync(EndpointRpcTestData.Token);
        await transport.FirstSendStarted;

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.SetLocalDeviceNameAsync(
                EndpointRpcTestData.Token,
                new string('N', DataLengthConstants.UserDeviceNameMaxLength + 1)));

        Assert.AreEqual(0, Volatile.Read(ref serializationCount));
        Assert.AreEqual(1, transport.SendCount);
        transport.Release();
        await admitted;
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationWhileWaitingForCapacitySerializesNothingAndLeaksNoPermit()
    {
        var transport = new BlockingEndpointRpcTransport();
        var serializationCount = 0;
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(LogoutEndpointRequest))
                Interlocked.Increment(ref serializationCount);
        });
        await using var proxy = CreateProxy(transport, serializer, maximumConcurrentOperations: 1);
        var first = proxy.LogoutAsync(EndpointRpcTestData.Token);
        await transport.FirstSendStarted;
        using var cancellationSource = new CancellationTokenSource();
        var cancelled = proxy.LogoutAsync(EndpointRpcTestData.Token, cancellationSource.Token);

        cancellationSource.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cancelled);
        Assert.AreEqual(1, Volatile.Read(ref serializationCount));
        Assert.AreEqual(1, transport.SendCount);

        transport.Release();
        await first;
        await proxy.LogoutAsync(EndpointRpcTestData.Token);
        Assert.AreEqual(2, Volatile.Read(ref serializationCount));
        Assert.AreEqual(2, transport.SendCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationDuringSerializationIsDefinitelyNotSentAndReleasesCapacity()
    {
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromResult("{}"u8.ToArray()));
        var serializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSerialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serializationCount = 0;
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type != typeof(LogoutEndpointRequest) ||
                Interlocked.Increment(ref serializationCount) != 1)
            {
                return;
            }

            serializationStarted.TrySetResult();
            releaseSerialization.Task.GetAwaiter().GetResult();
        });
        await using var proxy = CreateProxy(transport, serializer, maximumConcurrentOperations: 1);
        using var cancellationSource = new CancellationTokenSource();
        var cancelled = Task.Run(() =>
            proxy.LogoutAsync(EndpointRpcTestData.Token, cancellationSource.Token));
        await serializationStarted.Task;

        cancellationSource.Cancel();
        releaseSerialization.TrySetResult();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await cancelled);
        Assert.AreEqual(0, transport.Operations.Count);
        await proxy.LogoutAsync(EndpointRpcTestData.Token);
        Assert.AreEqual(1, transport.Operations.Count);
    }

    [TestMethod]
    public async Task SerializationValidationTransportAndResponseFailuresEachReleaseCapacity()
    {
        var serializationAttempt = 0;
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(LogoutEndpointRequest) &&
                Interlocked.Increment(ref serializationAttempt) == 1)
            {
                throw new IOException("serialization failed");
            }
        });
        var successTransport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromResult("{}"u8.ToArray()));
        await using (var proxy = CreateProxy(successTransport, serializer, 1))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                proxy.LogoutAsync(EndpointRpcTestData.Token));
            await proxy.LogoutAsync(EndpointRpcTestData.Token);
        }

        await using (var proxy = CreateProxy(successTransport, new EndpointRpcSerializer(), 1))
        {
            await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
                proxy.LogoutAsync(Guid.Empty));
            await proxy.LogoutAsync(EndpointRpcTestData.Token);
        }

        var transportAttempt = 0;
        var transportFailure = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            if (Interlocked.Increment(ref transportAttempt) == 1)
                return Task.FromException<byte[]>(new IOException("transport failed"));
            return Task.FromResult("{}"u8.ToArray());
        }, EndpointRpcTransmissionState.DefinitelyNotSent);
        await using (var proxy = CreateProxy(transportFailure, new EndpointRpcSerializer(), 1))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                proxy.LogoutAsync(EndpointRpcTestData.Token));
            await proxy.LogoutAsync(EndpointRpcTestData.Token);
        }

        var responseAttempt = 0;
        var responseFailure = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromResult(Interlocked.Increment(ref responseAttempt) == 1
                ? "{"u8.ToArray()
                : "{}"u8.ToArray()));
        await using (var proxy = CreateProxy(responseFailure, new EndpointRpcSerializer(), 1))
        {
            await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
                proxy.LogoutAsync(EndpointRpcTestData.Token));
            await proxy.LogoutAsync(EndpointRpcTestData.Token);
        }
    }

    [TestMethod]
    public void AdmissionLeaseReleasesItsPermitAtMostOnce()
    {
        using var capacity = new SemaphoreSlim(0, 1);
        var lease = new EndpointRpcAdmissionLease(capacity);

        lease.Dispose();
        lease.Dispose();

        Assert.IsTrue(capacity.Wait(0));
        Assert.IsFalse(capacity.Wait(0));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposalWaitsForAdmittedOperationsAndCancelsWaitingOperations()
    {
        var transport = new BlockingEndpointRpcTransport();
        var proxy = CreateProxy(transport, new EndpointRpcSerializer(), 1);
        var admitted = proxy.LogoutAsync(EndpointRpcTestData.Token);
        await transport.FirstSendStarted;
        var waiting = proxy.LogoutAsync(EndpointRpcTestData.Token);

        var disposal = proxy.DisposeAsync().AsTask();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waiting);
        Assert.IsFalse(disposal.IsCompleted);

        transport.Release();
        await admitted;
        await disposal;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            proxy.LogoutAsync(EndpointRpcTestData.Token));
    }

    private static NamedPipeEndpointsProxy CreateProxy(
        IEndpointRpcTransport transport,
        EndpointRpcSerializer serializer,
        int maximumConcurrentOperations) =>
        new(
            transport,
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcClientOptions(maximumConcurrentOperations));
}
