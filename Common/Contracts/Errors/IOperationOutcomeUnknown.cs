namespace PasswordManagerLocal.Common.Contracts.Errors;

/// <summary>The response was lost; callers must read current state before repeating a mutation.</summary>
public interface IOperationOutcomeUnknown
{
    bool BlindRetryIsSafe { get; }
}
