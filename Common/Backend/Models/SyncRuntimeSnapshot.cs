namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record SyncRuntimeSnapshot(
    SyncRuntimeState State,
    Exception? Failure);
