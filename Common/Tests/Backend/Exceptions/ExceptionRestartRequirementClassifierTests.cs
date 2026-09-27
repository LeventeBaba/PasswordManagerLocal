using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Backend.Exceptions;

[TestClass]
public sealed class ExceptionRestartRequirementClassifierTests
{
    [TestMethod]
    public void NestedAndAggregateRestartFailuresAreDetected()
    {
        var nested = new IOException(
            "outer wrapper",
            new InvalidOperationException(
                "inner wrapper",
                new DatabaseVersionNotSupportedException(1, 2, 3)));
        var aggregate = new AggregateException(
            new IOException("ordinary failure"),
            nested);

        Assert.IsTrue(ExceptionRestartRequirementClassifier.RequiresProcessRestart(nested));
        Assert.IsTrue(ExceptionRestartRequirementClassifier.RequiresProcessRestart(aggregate));
    }

    [TestMethod]
    public void OrdinaryExceptionChainsDoNotRequireRestart()
    {
        var nested = new IOException(
            "outer wrapper",
            new InvalidOperationException("ordinary inner failure"));
        var aggregate = new AggregateException(
            new IOException("first ordinary failure"),
            nested);

        Assert.IsFalse(ExceptionRestartRequirementClassifier.RequiresProcessRestart(null));
        Assert.IsFalse(ExceptionRestartRequirementClassifier.RequiresProcessRestart(nested));
        Assert.IsFalse(ExceptionRestartRequirementClassifier.RequiresProcessRestart(aggregate));
    }

    [TestMethod]
    public void PartialCommitConstructorCombinesExplicitAndNestedRestartRequirements()
    {
        var nestedRestart = new MutationPartiallyCommittedException(
            "partial commit",
            requiresProcessRestart: false,
            innerException: new DatabaseVersionNotSupportedException(1, 2, 3));
        var explicitRestart = new MutationPartiallyCommittedException(
            "partial commit",
            requiresProcessRestart: true,
            innerException: new IOException("ordinary failure"));
        var ordinary = new MutationPartiallyCommittedException(
            "partial commit",
            requiresProcessRestart: false,
            innerException: new IOException("ordinary failure"));

        var enrollmentNestedRestart = new DeviceEnrollmentPartiallyCommittedException(
            DeviceEnrollmentErrorCode.Unknown,
            "enrollment partial commit",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            recoveryAvailable: true,
            transferPending: true,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart: false,
            innerException: new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                "unsupported database"));

        Assert.IsTrue(nestedRestart.RequiresProcessRestart);
        Assert.IsTrue(explicitRestart.RequiresProcessRestart);
        Assert.IsFalse(ordinary.RequiresProcessRestart);
        Assert.IsTrue(enrollmentNestedRestart.RequiresProcessRestart);
    }

    [TestMethod]
    public void EnrollmentUnsupportedDatabaseWrapperRequiresRestart()
    {
        var failure = new DeviceEnrollmentException(
            DeviceEnrollmentErrorCode.Unknown,
            "outer enrollment failure",
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                "unsupported database"));

        Assert.IsTrue(ExceptionRestartRequirementClassifier.RequiresProcessRestart(failure));
    }
}
