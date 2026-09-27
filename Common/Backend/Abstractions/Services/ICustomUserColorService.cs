using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ICustomUserColorService
{
    IReadOnlyList<CustomUserColorInfoResponse> ConvertToCustomUserColorInfoResponses(UserPasswordsData passwords);
    void AddCustomUserColor(NewCustomUserColorRequest request, UserPasswordsData passwords);
    void AddCustomUserColors(IReadOnlyList<NewCustomUserColorRequest> requests, UserPasswordsData passwords);
    void DeleteCustomUserColors(IReadOnlyList<Guid> customUserColorIds, UserPasswordsData passwords);
    void ExportCustomUserColors(IReadOnlyList<Guid> customUserColorIds, UserPasswordsData sourcePasswords, UserPasswordsData targetPasswords);
    void UpdateCustomUserColor(UpdateCustomUserColorRequest request, UserPasswordsData passwords);
    CustomUserColor GetAndVerifyCustomUserColorById(Guid customUserColorId, UserPasswordsData passwords);
}
