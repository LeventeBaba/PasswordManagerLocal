namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class SelfDiagnosticsResultResponse
{
    public bool Healthy { get; set; }
    public int RepairedCount { get; set; }
    public IReadOnlyList<string> Findings { get; set; } = [];
}
