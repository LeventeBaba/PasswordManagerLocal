using System.Text;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal static class RecoveryHistoryTestData
{
    public static async Task BreakRemovedDeviceHistoryAsync(IServiceProvider services, Guid userId, Guid deviceId)
    {
        var authorizations = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        var authorization = (await authorizations.ListForUserAsync(userId)).Single(row => row.DeviceId == deviceId);
        authorization.IsActive = true; // Stale authorization without a corresponding visible link.
        authorization.Version++;
        authorizations.Update(authorization);

        var links = services.GetRequiredService<IUserDeviceRepository>();
        var link = await links.GetAsync(userId, deviceId)
            ?? throw new InvalidOperationException("The fixture must contain the removed device link.");
        links.Delete(link);

        var operations = services.GetRequiredService<IUserControlOperationRepository>();
        var removal = (await operations.ListForUserAsync(userId))
            .Single(row => row.OperationType == UserControlOperationType.DeviceRemoval);
        removal.EnvelopePayload = Encoding.UTF8.GetBytes("{broken history");
        operations.Update(removal);

        var commits = services.GetRequiredService<IDeviceEnrollmentCommitRepository>();
        var commit = (await commits.ListForUserAsync(userId)).Single();
        commit.Status = DeviceEnrollmentCommitStatus.TransferFailed;
        commits.Update(commit);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }
}
