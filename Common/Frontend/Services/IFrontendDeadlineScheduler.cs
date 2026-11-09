namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Schedules one UI-session callback at an absolute UTC deadline.</summary>
public interface IFrontendDeadlineScheduler
{
    IDisposable Schedule(DateTimeOffset deadlineUtc, Action callback);
}
