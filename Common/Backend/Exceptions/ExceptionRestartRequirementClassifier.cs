using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Exceptions;

public static class ExceptionRestartRequirementClassifier
{
    public static bool RequiresProcessRestart(Exception? exception)
    {
        if (exception is null)
            return false;

        return RequiresProcessRestart(
            exception,
            new HashSet<Exception>(ReferenceEqualityComparer.Instance));
    }

    private static bool RequiresProcessRestart(
        Exception exception,
        ISet<Exception> visited)
    {
        // Reference tracking keeps malformed exception graphs from recursing indefinitely.
        if (!visited.Add(exception))
            return false;

        if (exception is DatabaseVersionNotSupportedException)
            return true;

        if (exception is MutationPartiallyCommittedException partialCommitException &&
            partialCommitException.RequiresProcessRestart)
        {
            return true;
        }

        if (exception is DeviceEnrollmentException enrollmentException &&
            enrollmentException.ErrorCode == DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                if (RequiresProcessRestart(innerException, visited))
                    return true;
            }

            return false;
        }

        return exception.InnerException is not null &&
            RequiresProcessRestart(exception.InnerException, visited);
    }
}
