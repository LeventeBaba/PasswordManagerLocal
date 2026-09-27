using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataPersistenceValidator
{
    void EnsureUserDataCanBePersisted(UserData userData, User user);
    void EnsureUserDataBundleCanBePersisted(UserDataBundle bundle, User user);
}
