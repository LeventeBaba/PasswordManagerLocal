namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DuplicatePasswordTagNameException : Exception
{
    public string TagName { get; }

    public DuplicatePasswordTagNameException(string tagName)
        : base($"Password tag name already exists: {tagName}")
    {
        TagName = tagName;
    }
}
