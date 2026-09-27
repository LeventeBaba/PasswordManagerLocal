namespace PasswordManagerLocal.Windows.Packaging;

public sealed record StagedCollisionAnalysis(
    string SchemaVersion,
    IReadOnlyList<StagedCollision> Collisions,
    IReadOnlyList<string> CopyUsedAssemblies);
