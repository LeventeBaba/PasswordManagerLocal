namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class PendingSecurityOperationException()
    : Exception("This profile is temporarily unavailable while a security change is being saved. Free local storage if needed and retry synchronization.");
