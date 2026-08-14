namespace PasswordManagerLocal.Windows.Packaging;

public sealed record PackageSummary(
    int FrontendStagedFiles,
    long FrontendStagedBytes,
    int AgentStagedFiles,
    long AgentStagedBytes,
    int SharedIdenticalFiles,
    long SharedIdenticalBytes,
    int FrontendOnlyFiles,
    long FrontendOnlyBytes,
    int AgentOnlyFiles,
    long AgentOnlyBytes,
    int CollisionCount,
    long BytesRemovedThroughDeduplication,
    int FinalFileCount,
    long FinalSizeBytes);
