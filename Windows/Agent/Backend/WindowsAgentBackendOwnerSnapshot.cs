using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Agent.Backend;

public sealed record WindowsAgentBackendOwnerSnapshot(
    WindowsAgentBackendOwnerState State,
    BackendRuntimeSnapshot Runtime,
    InteractiveSessionLifecycleSnapshot InteractiveSession,
    SyncRuntimeSnapshot Synchronization,
    BackendLifetimeReason ActiveReasons,
    bool RequiresProcessRestart,
    bool IsResetting,
    Exception? Failure,
    DateTimeOffset ChangedAtUtc);
