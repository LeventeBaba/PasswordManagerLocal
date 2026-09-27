using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Server;

[TestClass]
public sealed class EndpointRpcBackendErrorMapperRestartTests
{
    private readonly EndpointRpcBackendErrorMapper _mapper = new();

    [TestMethod]
    public void NestedRestartFailureBeforeCommitMapsToConclusiveRuntimeFailure()
    {
        var failure = new IOException(
            "outer runtime wrapper",
            new InvalidOperationException(
                "inner runtime wrapper",
                CreateDatabaseVersionFailure()));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.Logout));

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, error.MutationOutcome);
        Assert.IsTrue(error.RequiresProcessRestart);
        Assert.IsNull(error.Recovery);
    }

    [TestMethod]
    public void AggregateRestartFailureBeforeReadMapsToConclusiveRuntimeFailure()
    {
        var failure = new AggregateException(
            new IOException("ordinary failure"),
            CreateDatabaseVersionFailure());

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.GetLocalDeviceInfo));

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotApplicable, error.MutationOutcome);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void GenericPartialCommitWithDirectRestartFailurePreservesRestartRequirement()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            innerException: CreateDatabaseVersionFailure());

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, error.MutationOutcome);
        Assert.IsTrue(error.RequiresProcessRestart);
        Assert.IsNull(error.Recovery);
    }

    [TestMethod]
    public void GenericPartialCommitWithMultipleNestedWrappersPreservesRestartRequirement()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            innerException: new InvalidOperationException(
                "outer follow-up wrapper",
                new IOException("inner follow-up wrapper", CreateDatabaseVersionFailure())));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void GenericPartialCommitWithRestartFailureInAggregatePreservesRestartRequirement()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            innerException: new AggregateException(
                new IOException("ordinary failure"),
                CreateDatabaseVersionFailure()));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void GenericPartialCommitWithOrdinaryAggregateDoesNotInventRestartRequirement()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            innerException: new AggregateException(
                new IOException("first ordinary failure"),
                new InvalidOperationException("second ordinary failure")));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsFalse(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void GenericPartialCommitExplicitRestartRemainsTrueWithOrdinaryInnerFailure()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            requiresProcessRestart: true,
            innerException: new IOException("ordinary failure"));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void GenericPartialCommitOrdinaryInnerFailureRemainsRestartFalse()
    {
        var failure = new MutationPartiallyCommittedException(
            "authoritative state committed",
            requiresProcessRestart: false,
            innerException: new IOException("ordinary failure"));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsFalse(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void EnrollmentPartialCommitWithNestedUnsupportedDatabaseFailureRequiresRestart()
    {
        var failure = CreateEnrollmentPartialCommit(
            requiresProcessRestart: false,
            innerException: new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.Unknown,
                "outer enrollment wrapper",
                new IOException(
                    "nested transfer wrapper",
                    new DeviceEnrollmentException(
                        DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                        "unsupported database"))));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.AddDeviceByCode));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, error.MutationOutcome);
        Assert.IsTrue(error.RequiresProcessRestart);
        Assert.IsNotNull(error.Recovery);
    }

    [TestMethod]
    public void EnrollmentPartialCommitExplicitRestartRemainsTrueWithOrdinaryInnerFailure()
    {
        var failure = CreateEnrollmentPartialCommit(
            requiresProcessRestart: true,
            innerException: new IOException("ordinary transfer failure"));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.AddDeviceByCode));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    public void EnrollmentPartialCommitOrdinaryTransferFailureRemainsRestartFalse()
    {
        var failure = CreateEnrollmentPartialCommit(
            requiresProcessRestart: false,
            innerException: new IOException("ordinary transfer failure"));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.AddDeviceByCode));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.IsFalse(error.RequiresProcessRestart);
    }

    private static DeviceEnrollmentPartiallyCommittedException CreateEnrollmentPartialCommit(
        bool requiresProcessRestart,
        Exception innerException) =>
        new(
            DeviceEnrollmentErrorCode.Unknown,
            "post-commit enrollment failure",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            recoveryAvailable: true,
            transferPending: true,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart: requiresProcessRestart,
            innerException: innerException);

    private static DatabaseVersionNotSupportedException CreateDatabaseVersionFailure() =>
        new(1, 2, 3);

    private static EndpointRequestContext CreateContext(EndpointOperationId operationId) =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            1,
            operationId,
            IpcPeerRole.Ui,
            1234,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            CancellationToken.None);
}
