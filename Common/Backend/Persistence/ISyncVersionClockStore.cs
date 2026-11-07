using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal interface ISyncVersionClockStore
{
    string DatabaseKey { get; }
    SyncVersionClockState? Read(int? timeoutSeconds = null);
    bool TryWrite(SyncVersionClockState state, long? expectedVersion, int? timeoutSeconds = null);
}
