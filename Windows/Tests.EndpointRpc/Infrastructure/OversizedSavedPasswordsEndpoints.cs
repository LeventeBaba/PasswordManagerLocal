using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class OversizedSavedPasswordsEndpoints : ThrowingRecordingEndpoints
{
    public override Task<SavedPasswordsResponse> GetSavedPasswordsAsync(
        Guid token,
        CancellationToken ct = default)
    {
        var description = new string('€', 1000);
        var passwords = Enumerable.Range(0, 1000)
            .Select(index => new PasswordInfoResponse
            {
                Id = Guid.NewGuid(),
                Name = $"Entry{index}",
                Description = description,
                Color = "#FF14B8A6",
                TagIds = [],
                CreatedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc),
                LastUpdatedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 2), DateTimeKind.Utc)
            })
            .ToArray();
        return Task.FromResult(new SavedPasswordsResponse { Passwords = passwords });
    }
}
