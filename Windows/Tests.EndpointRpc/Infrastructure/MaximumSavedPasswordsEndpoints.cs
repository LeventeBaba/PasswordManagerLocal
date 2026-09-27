using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class MaximumSavedPasswordsEndpoints : ThrowingRecordingEndpoints
{
    private readonly SavedPasswordsResponse _response;

    public MaximumSavedPasswordsEndpoints()
    {
        var tagIds = Enumerable.Range(1, PasswordConstants.MaxNumberOfPasswordTags)
            .Select(index => CreateGuid(1, index))
            .ToArray();
        var description = new string('€', DataLengthConstants.DescriptionMaxLength);
        var passwordName = new string('P', DataLengthConstants.PasswordNameMaxLength);
        var tagName = new string('T', DataLengthConstants.PasswordTagNameMaxLength);
        var colorName = new string('C', DataLengthConstants.CustomUserColorNameMaxLength);
        var createdAt = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);
        var updatedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 2), DateTimeKind.Utc);

        _response = new SavedPasswordsResponse
        {
            Passwords = Enumerable.Range(1, PasswordConstants.MaxNumberOfPasswords)
                .Select(index => new PasswordInfoResponse
                {
                    Id = CreateGuid(2, index),
                    Name = passwordName,
                    Description = description,
                    Color = PasswordConstants.DefaultPasswordColor,
                    TagIds = tagIds,
                    CreatedAt = createdAt,
                    LastUpdatedAt = updatedAt
                })
                .ToArray(),
            Tags = tagIds.Select((id, index) => new PasswordTagInfoResponse
                {
                    Id = id,
                    Name = tagName,
                    Color = PasswordConstants.DefaultPasswordColor,
                    LastUpdatedAt = updatedAt
                })
                .ToArray(),
            CustomColors = Enumerable.Range(1, PasswordConstants.MaxNumberOfCustomUserColors)
                .Select(index => new CustomUserColorInfoResponse
                {
                    Id = CreateGuid(3, index),
                    ColorName = colorName,
                    ColorCode = $"#{index % 0x100000000L:X8}",
                    LastUpdatedAt = updatedAt
                })
                .ToArray()
        };
    }

    public int InvocationCount { get; private set; }
    public SavedPasswordsResponse ExpectedResponse => _response;

    public override Task<SavedPasswordsResponse> GetSavedPasswordsAsync(
        Guid token,
        CancellationToken ct = default)
    {
        InvocationCount++;
        return Task.FromResult(_response);
    }

    private static Guid CreateGuid(int family, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, family);
        BitConverter.TryWriteBytes(bytes[4..], index);
        bytes[8] = 0x80;
        bytes[15] = 0x01;
        return new Guid(bytes);
    }
}
