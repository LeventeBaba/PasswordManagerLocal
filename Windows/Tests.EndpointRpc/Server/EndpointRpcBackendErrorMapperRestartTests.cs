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
    [DataRow(EndpointOperationId.Logout, "runtime-nested-restart", EndpointMutationOutcome.NotCommitted)]
    [DataRow(EndpointOperationId.GetLocalDeviceInfo, "aggregate-restart", EndpointMutationOutcome.NotApplicable)]
    public void PreInvocationRestartFailureIsConclusiveForOperationKind(
        EndpointOperationId operation, string shape, EndpointMutationOutcome expectedOutcome)
    {
        var error = _mapper.Map(CreateFollowUpFailure(shape), CreateContext(operation));

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, error.ErrorCode);
        Assert.AreEqual(expectedOutcome, error.MutationOutcome);
        Assert.AreEqual(EndpointFailureKind.Generic, error.FailureKind);
        Assert.IsTrue(error.RequiresProcessRestart);
    }

    [TestMethod]
    [DataRow("direct-restart", false, true)]
    [DataRow("nested-restart", false, true)]
    [DataRow("aggregate-restart", false, true)]
    [DataRow("ordinary-aggregate", false, false)]
    [DataRow("ordinary", true, true)]
    [DataRow("ordinary", false, false)]
    public void GenericPartialCommitCombinesExplicitAndNestedRestartRequirements(
        string shape, bool explicitRestart, bool expectedRestart)
    {
        var failure = new MutationPartiallyCommittedException("authoritative state committed",
            requiresProcessRestart: explicitRestart, innerException: CreateFollowUpFailure(shape));

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.ChangeMasterPassword));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, error.MutationOutcome);
        Assert.AreEqual(EndpointFailureKind.Generic, error.FailureKind);
        Assert.AreEqual(expectedRestart, error.RequiresProcessRestart);
    }

    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(false, false, false)]
    public void EnrollmentPartialCommitCombinesExplicitAndNestedRestartRequirements(
        bool nestedUnsupportedDatabase, bool explicitRestart, bool expectedRestart)
    {
        Exception inner = nestedUnsupportedDatabase
            ? new DeviceEnrollmentException(DeviceEnrollmentErrorCode.Unknown, "outer enrollment wrapper",
                new IOException("nested transfer wrapper", new DeviceEnrollmentException(
                    DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion, "unsupported database")))
            : new IOException("ordinary transfer failure");
        var failure = CreateEnrollmentPartialCommit(explicitRestart, inner);

        var error = _mapper.Map(failure, CreateContext(EndpointOperationId.AddDeviceByCode));

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, error.MutationOutcome);
        Assert.AreEqual(EndpointFailureKind.DeviceEnrollment, error.FailureKind);
        Assert.AreEqual(DeviceEnrollmentErrorCode.Unknown, error.EnrollmentErrorCode);
        Assert.AreEqual(expectedRestart, error.RequiresProcessRestart);
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

    private static Exception CreateFollowUpFailure(string shape) => shape switch
    {
        "direct-restart" => CreateDatabaseVersionFailure(),
        "runtime-nested-restart" => new IOException("outer runtime wrapper",
            new InvalidOperationException("inner runtime wrapper", CreateDatabaseVersionFailure())),
        "nested-restart" => new InvalidOperationException("outer wrapper",
            new IOException("inner wrapper", CreateDatabaseVersionFailure())),
        "aggregate-restart" => new AggregateException(new IOException("ordinary failure"), CreateDatabaseVersionFailure()),
        "ordinary-aggregate" => new AggregateException(new IOException("ordinary failure"), new InvalidOperationException("other ordinary failure")),
        "ordinary" => new IOException("ordinary failure"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

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
