namespace PasswordManagerLocal.Windows.Packaging;

public sealed class PackagingCollisionException : InvalidOperationException
{
    public PackagingCollisionException(string message) : base(message)
    {
    }
}
