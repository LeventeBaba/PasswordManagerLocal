using PasswordManagerLocal.Common.Backend.Sync;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentSnapshotImporterService
{
    Task ImportAsync(IServiceProvider services, DeviceEnrollmentSnapshot snapshot, CancellationToken ct = default);
}
