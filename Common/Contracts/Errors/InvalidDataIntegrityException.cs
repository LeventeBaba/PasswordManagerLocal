namespace PasswordManagerLocal.Common.Contracts.Errors;

public class InvalidDataIntegrityException : Exception
{
    public Type ObjectType { get; private set; }

    public InvalidDataIntegrityException(Type objectType) : base($"Data integrity check failed. {objectType} is corrupt or missing.")
    {
        ObjectType = objectType;
    }
}
