namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class CustomUserColorNotFoundException : Exception
{
    public Guid CustomUserColorId { get; }

    public CustomUserColorNotFoundException(Guid customUserColorId)
        : base($"Custom user color {customUserColorId} was not found.")
    {
        CustomUserColorId = customUserColorId;
    }
}
