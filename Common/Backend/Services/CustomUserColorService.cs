using PasswordManagerLocal.Common.Backend.Mapping;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;
using PasswordManagerLocal.Common.Backend.Utils;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class CustomUserColorService : ICustomUserColorService
{
    private readonly ISyncVersionClockService _versionClock;

    public CustomUserColorService() : this(new EphemeralSyncVersionClockService()) { }

    public CustomUserColorService(ISyncVersionClockService versionClock)
    {
        _versionClock = versionClock;
    }

    public IReadOnlyList<CustomUserColorInfoResponse> ConvertToCustomUserColorInfoResponses(UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        return passwords.CustomColors
            .OrderBy(color => color.ColorName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(color => color.ColorCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(color => color.Id)
            .Select(EndpointResponseMapper.ToCustomUserColorInfoResponse)
            .ToList();
    }


    public void AddCustomUserColor(NewCustomUserColorRequest request, UserPasswordsData passwords) =>
        AddCustomUserColors([request], passwords);


    public void AddCustomUserColors(IReadOnlyList<NewCustomUserColorRequest> requests, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        if (requests.Count == 0)
            return;

        if (passwords.CustomColors.Count + requests.Count > MaxNumberOfCustomUserColors)
            throw new LimitReachedException(MaxNumberOfCustomUserColors, "customUserColor");

        var normalizedColors = NormalizeAndValidateNewCustomColors(requests, passwords);
        var now = DateTime.UtcNow;

        foreach (var (colorName, colorCode) in normalizedColors)
        {
            var customColor = new CustomUserColor
            {
                Id = Guid.NewGuid(),
                ColorName = colorName,
                ColorCode = colorCode,
                LastUpdatedAt = now,
                Version = _versionClock.Next()
            };
            customColor.GenerateIntegrityHash();
            passwords.CustomColors.Add(customColor);
        }

        passwords.GenerateCustomColorsIntegrityHash();
    }


    public void DeleteCustomUserColors(IReadOnlyList<Guid> customUserColorIds, UserPasswordsData passwords)
    {
        ArgumentNullException.ThrowIfNull(customUserColorIds);
        passwords.VerifyIntegrity();

        if (customUserColorIds.Count == 0)
            return;

        var uniqueCustomUserColorIds = customUserColorIds.Distinct().ToList();
        var colorsToDelete = new List<CustomUserColor>(uniqueCustomUserColorIds.Count);

        foreach (var customUserColorId in uniqueCustomUserColorIds)
        {
            var color = passwords.CustomColors.FirstOrDefault(color => color.Id == customUserColorId);
            if (color is null)
                throw new CustomUserColorNotFoundException(customUserColorId);

            color.VerifyIntegrity();
            colorsToDelete.Add(color);
        }

        var deletedAt = DateTime.UtcNow;
        foreach (var color in colorsToDelete)
        {
            TombstoneCleanupUtil.AddOrUpdateDeletedCustomUserColor(passwords, color.Id, deletedAt, _versionClock.Next());
            passwords.CustomColors.Remove(color);
            color.Dispose();
        }

        passwords.GenerateCustomColorsIntegrityHash();
    }


    public void ExportCustomUserColors(
        IReadOnlyList<Guid> customUserColorIds,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords)
    {
        ArgumentNullException.ThrowIfNull(customUserColorIds);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();

        ValidateCustomUserColorExportRequest(customUserColorIds, targetPasswords);

        var selectedColors = GetCustomUserColorsToExport(customUserColorIds, sourcePasswords);
        EnsureTargetCustomUserColorsAreAvailable(selectedColors, targetPasswords);

        var now = DateTime.UtcNow;
        var copiedColors = selectedColors
            .Select(color =>
            {
                var copiedColor = new CustomUserColor
                {
                    Id = Guid.NewGuid(),
                    ColorName = NormalizeOptionalColorName(color.ColorName),
                    ColorCode = NormalizeColorCode(color.ColorCode),
                    LastUpdatedAt = now,
                    Version = _versionClock.Next()
                };
                copiedColor.GenerateIntegrityHash();
                return copiedColor;
            })
            .ToList();

        try
        {
            foreach (var copiedColor in copiedColors)
            {
                targetPasswords.DeletedCustomColors.RemoveAll(deleted => deleted.Id == copiedColor.Id);
                targetPasswords.CustomColors.Add(copiedColor);
            }

            targetPasswords.GenerateCustomColorsIntegrityHash();
        }
        catch
        {
            var copiedIds = copiedColors.Select(color => color.Id).ToHashSet();
            targetPasswords.CustomColors.RemoveAll(color => copiedIds.Contains(color.Id));
            copiedColors.ForEach(color => color.Dispose());
            throw;
        }
    }


    public void UpdateCustomUserColor(UpdateCustomUserColorRequest request, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var color = GetAndVerifyCustomUserColorById(request.Id, passwords);

        var newColorName = color.ColorName;
        if (request.ClearColorName)
            newColorName = null;
        else if (request.ColorName is not null)
            newColorName = NormalizeOptionalColorName(request.ColorName);

        var newColorCode = color.ColorCode;
        if (request.ColorCode is not null)
            newColorCode = NormalizeColorCode(request.ColorCode);

        ThrowIfCustomColorNameExists(newColorName, passwords, color.Id);
        ThrowIfCustomColorCodeExists(newColorCode, passwords, color.Id);

        color.ColorName = newColorName;
        color.ColorCode = newColorCode;
        passwords.DeletedCustomColors.RemoveAll(deleted => deleted.Id == color.Id);
        color.LastUpdatedAt = DateTime.UtcNow;
        color.Version = _versionClock.Next();
        color.GenerateIntegrityHash();
        passwords.GenerateCustomColorsIntegrityHash();
    }


    public CustomUserColor GetAndVerifyCustomUserColorById(Guid customUserColorId, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        var color = passwords.CustomColors.FirstOrDefault(color => color.Id == customUserColorId);
        if (color is null)
            throw new CustomUserColorNotFoundException(customUserColorId);

        color.VerifyIntegrity();
        return color;
    }


    private void ValidateCustomUserColorExportRequest(
        IReadOnlyList<Guid> customUserColorIds,
        UserPasswordsData targetPasswords)
    {
        var hasInvalidInput = customUserColorIds.Count == 0
            || customUserColorIds.Count > MaxNumberOfCustomUserColors
            || customUserColorIds.Any(id => id == Guid.Empty)
            || customUserColorIds.Distinct().Count() != customUserColorIds.Count;

        if (hasInvalidInput)
            throw new InvalidInputException(["CustomUserColorIds"]);

        if (targetPasswords.CustomColors.Count + customUserColorIds.Count > MaxNumberOfCustomUserColors)
            throw new LimitReachedException(MaxNumberOfCustomUserColors, "customUserColor");
    }


    private List<CustomUserColor> GetCustomUserColorsToExport(
        IReadOnlyList<Guid> customUserColorIds,
        UserPasswordsData sourcePasswords)
    {
        var selectedColors = new List<CustomUserColor>(customUserColorIds.Count);
        foreach (var customUserColorId in customUserColorIds)
        {
            var color = sourcePasswords.CustomColors.FirstOrDefault(color => color.Id == customUserColorId);
            if (color is null)
                throw new CustomUserColorNotFoundException(customUserColorId);

            color.VerifyIntegrity();
            selectedColors.Add(color);
        }

        return selectedColors;
    }


    private void EnsureTargetCustomUserColorsAreAvailable(
        IReadOnlyList<CustomUserColor> selectedColors,
        UserPasswordsData targetPasswords)
    {
        var usedNames = targetPasswords.CustomColors
            .Where(color => color.ColorName is not null)
            .Select(color => color.ColorName!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedCodes = targetPasswords.CustomColors
            .Select(color => NormalizeColorCode(color.ColorCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var color in selectedColors)
        {
            var colorName = NormalizeOptionalColorName(color.ColorName);
            var colorCode = NormalizeColorCode(color.ColorCode);

            if (colorName is not null && !usedNames.Add(colorName))
                throw new DuplicateCustomUserColorNameException(colorName);

            if (!usedCodes.Add(colorCode))
                throw new DuplicateCustomUserColorCodeException(colorCode);
        }
    }


    private List<(string? ColorName, string ColorCode)> NormalizeAndValidateNewCustomColors(
        IReadOnlyList<NewCustomUserColorRequest> requests,
        UserPasswordsData passwords)
    {
        var usedNames = passwords.CustomColors
            .Where(color => color.ColorName is not null)
            .Select(color => color.ColorName!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedCodes = passwords.CustomColors
            .Select(color => NormalizeColorCode(color.ColorCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedColors = new List<(string? ColorName, string ColorCode)>(requests.Count);

        foreach (var request in requests)
        {
            if (!request.Validate(out var errors))
                throw new InvalidInputException(errors);

            var colorName = NormalizeOptionalColorName(request.ColorName);
            var colorCode = NormalizeColorCode(request.ColorCode);

            if (colorName is not null && !usedNames.Add(colorName))
                throw new DuplicateCustomUserColorNameException(colorName);

            if (!usedCodes.Add(colorCode))
                throw new DuplicateCustomUserColorCodeException(colorCode);

            normalizedColors.Add((colorName, colorCode));
        }

        return normalizedColors;
    }


    private string NormalizeColorCode(string colorCode) => colorCode.Trim().ToUpperInvariant();


    private string? NormalizeOptionalColorName(string? colorName)
    {
        if (colorName is null)
            return null;

        var trimmed = colorName.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }


    private void ThrowIfCustomColorNameExists(
        string? colorName,
        UserPasswordsData passwords,
        Guid? ignoredCustomColorId = null)
    {
        if (colorName is null)
            return;

        var normalizedColorName = colorName.Trim();
        var exists = passwords.CustomColors.Any(color =>
            color.ColorName is not null
            && (!ignoredCustomColorId.HasValue || color.Id != ignoredCustomColorId.Value)
            && string.Equals(color.ColorName.Trim(), normalizedColorName, StringComparison.OrdinalIgnoreCase));

        if (exists)
            throw new DuplicateCustomUserColorNameException(normalizedColorName);
    }


    private void ThrowIfCustomColorCodeExists(
        string colorCode,
        UserPasswordsData passwords,
        Guid? ignoredCustomColorId = null)
    {
        var exists = passwords.CustomColors.Any(color =>
            (!ignoredCustomColorId.HasValue || color.Id != ignoredCustomColorId.Value)
            && string.Equals(NormalizeColorCode(color.ColorCode), colorCode, StringComparison.OrdinalIgnoreCase));

        if (exists)
            throw new DuplicateCustomUserColorCodeException(colorCode);
    }
}
