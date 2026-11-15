namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

/// <summary>A cheap process-local check. False requires cleanup before restarting the owner.</summary>
public interface ISyncRuntimeHealth
{
    bool IsHealthy { get; }
    bool RequiresProcessRestart => false;
}
