using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IAuthenticatedSessionIssuer
{
    Guid IssueAuthenticatedSession(Guid userId, EncryptionKey key, UserDataBundle bundle);
}
