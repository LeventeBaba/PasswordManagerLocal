namespace PasswordManagerLocal.Windows.Packaging;

public sealed record BaselineAnalysis(
    long? TotalProductBytes,
    long? FrontendBytes,
    long? AgentBytes,
    int FrontendFileCount,
    int AgentFileCount,
    int DuplicateNameCandidateCount,
    string Note);
