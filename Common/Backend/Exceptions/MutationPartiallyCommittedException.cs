namespace PasswordManagerLocal.Common.Backend.Exceptions;

public class MutationPartiallyCommittedException : Exception, IPartiallyCommittedOperation
{
    public MutationPartiallyCommittedException(
        string message,
        bool requiresProcessRestart = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RequiresProcessRestart = requiresProcessRestart ||
            ExceptionRestartRequirementClassifier.RequiresProcessRestart(innerException);
    }

    public bool RequiresProcessRestart { get; }
}
