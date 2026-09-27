using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncVersionClockService
{
    SyncVersionStamp Next();
    void Observe(IEnumerable<SyncVersionStamp> stamps);
}
