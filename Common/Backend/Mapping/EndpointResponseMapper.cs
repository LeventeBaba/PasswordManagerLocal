using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Mapping;

internal static class EndpointResponseMapper
{
    public static PasswordInfoResponse ToPasswordInfoResponse(SecurePassword password) => new()
    {
        Id = password.Id,
        Name = password.Name,
        Description = password.Description,
        Color = password.Color,
        TagIds = password.TagIds.ToList(),
        CreatedAt = UtcDateTimeUtil.ToUtc(password.CreatedAt),
        LastUpdatedAt = UtcDateTimeUtil.ToUtc(password.LastUpdatedAt)
    };

    public static CustomUserColorInfoResponse ToCustomUserColorInfoResponse(CustomUserColor color) => new()
    {
        Id = color.Id,
        ColorName = color.ColorName,
        ColorCode = color.ColorCode,
        LastUpdatedAt = UtcDateTimeUtil.ToUtc(color.LastUpdatedAt)
    };

    public static PasswordTagInfoResponse ToPasswordTagInfoResponse(PasswordTag tag) => new()
    {
        Id = tag.Id,
        Name = tag.Name,
        Color = tag.Color,
        LastUpdatedAt = UtcDateTimeUtil.ToUtc(tag.LastUpdatedAt)
    };

    public static UserProfileInfoResponse ToUserProfileInfoResponse(
        UserData userData,
        GeneralUserData generalUserData,
        bool isRememberMeEnabled = false) => new()
    {
        UId = userData.UId,
        Username = generalUserData.Username,
        FirstName = generalUserData.FirstName,
        LastName = generalUserData.LastName,
        Email = generalUserData.Email,
        RegistrationDate = UtcDateTimeUtil.ToUtc(generalUserData.RegistrationDate),
        RegistrationTimeZoneId = generalUserData.RegistrationTimeZoneId,
        RegistrationDeviceType = generalUserData.RegistrationDeviceType,
        IsRememberMeEnabled = isRememberMeEnabled
    };
}
