using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Test-only fallback for directly constructed mutation services. Production DI uses the durable clock.</summary>
internal sealed class EphemeralSyncVersionClockService : ISyncVersionClockService
{
    private static readonly object Gate = new();
    private static readonly Guid FallbackDeviceId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FallbackInstanceId = new("22222222-2222-2222-2222-222222222222");
    private static long _physical;
    private static long _logical;

    private readonly IDeviceIdentityService? _identity;

    public EphemeralSyncVersionClockService()
    {
    }

    public EphemeralSyncVersionClockService(IDeviceIdentityService identity) =>
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    public SyncVersionStamp Next()
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now > _physical)
            {
                _physical = now;
                _logical = 0;
            }
            else
            {
                _logical++;
            }
            var deviceId = _identity is { IsInitialized: true, LocalDeviceId: var localDeviceId } && localDeviceId != Guid.Empty
                ? localDeviceId
                : FallbackDeviceId;
            var originInstanceId = _identity is { IsInitialized: true, OriginInstanceId: var localOriginInstanceId } && localOriginInstanceId != Guid.Empty
                ? localOriginInstanceId
                : FallbackInstanceId;

            return new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = _physical,
                LogicalCounter = _logical,
                OriginDeviceId = deviceId,
                OriginInstanceId = originInstanceId
            };
        }
    }

    public void Observe(IEnumerable<SyncVersionStamp> stamps)
    {
        lock (Gate)
        {
            foreach (var stamp in stamps)
            {
                SyncVersionStampComparer.Validate(stamp);
                if (stamp.PhysicalTimeUnixMilliseconds > _physical)
                {
                    _physical = stamp.PhysicalTimeUnixMilliseconds;
                    _logical = stamp.LogicalCounter;
                }
                else if (stamp.PhysicalTimeUnixMilliseconds == _physical)
                {
                    _logical = Math.Max(_logical, stamp.LogicalCounter);
                }
            }
        }
    }
}
