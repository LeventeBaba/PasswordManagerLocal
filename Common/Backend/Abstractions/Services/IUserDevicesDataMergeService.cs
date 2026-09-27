using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDevicesDataMergeService
{
    bool Merge(UserDevicesData local, UserDevicesData incoming);
}
