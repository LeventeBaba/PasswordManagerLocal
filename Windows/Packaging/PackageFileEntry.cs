namespace PasswordManagerLocal.Windows.Packaging;

public sealed record PackageFileEntry(
    string RelativePath,
    PackageFileSource Source,
    long Length,
    string Sha256,
    PackageMergeAction Action,
    string Classification);
