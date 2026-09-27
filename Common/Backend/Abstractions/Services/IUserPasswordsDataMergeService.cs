using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserPasswordsDataMergeService
{
    bool Merge(UserPasswordsData local, UserPasswordsData incoming);
}
