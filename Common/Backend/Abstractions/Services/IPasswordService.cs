using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IPasswordService
{
    IReadOnlyList<PasswordInfoResponse> ConvertToPasswordInfoResponses(UserPasswordsData passwords);
    Task AddNewPassword(NewPasswordRequest request, UserPasswordsData passwords);
    void RemovePasswords(IReadOnlyList<Guid> passwordIds, UserPasswordsData passwords);
    SecurePassword GetAndVerifyPasswordById(Guid passwordId, UserPasswordsData passwords);
    Task<byte[]> GetUnsecurePasswordAsync(Guid passwordId, UserPasswordsData passwords);
    Task UpdatePasswordAsync(UpdatePasswordRequest request, UserPasswordsData passwords);
    Task ExportPasswordsAsync(IReadOnlyList<Guid> passwordIds, UserPasswordsData sourcePasswords, UserPasswordsData targetPasswords);
    Task<byte[]> EncryptPasswordAsync(byte[] raw, UserPasswordsData passwords);
    Task<byte[]> DecryptPasswordAsync(byte[] password, UserPasswordsData passwords);
}
