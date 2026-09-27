namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class SavedPasswordsResponse
{
    public IReadOnlyList<PasswordInfoResponse> Passwords { get; set; } = [];
    public IReadOnlyList<CustomUserColorInfoResponse> CustomColors { get; set; } = [];
    public IReadOnlyList<PasswordTagInfoResponse> Tags { get; set; } = [];
}
