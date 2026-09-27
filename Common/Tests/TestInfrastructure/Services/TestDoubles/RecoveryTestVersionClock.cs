using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class RecoveryTestVersionClock : ISyncVersionClockService
{
    public SyncVersionStamp Next() => throw new InvalidOperationException("Recovery must not create new deterministic item versions.");

    public void Observe(IEnumerable<SyncVersionStamp> stamps)
    {
        foreach (var stamp in stamps)
            SyncVersionStampComparer.Validate(stamp);
    }
}
