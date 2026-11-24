namespace PasswordManagerLocal.Common.Contracts.Errors;

/// <summary>The mutation committed; follow-up work failed and the mutation must not be blindly repeated.</summary>
public interface IPartiallyCommittedOperation
{
    bool RequiresProcessRestart { get; }
}
