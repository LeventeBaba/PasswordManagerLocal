using PasswordManagerLocal.Common.Backend.Mapping;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class PasswordTagService : IPasswordTagService
{
    private readonly ISyncVersionClockService _versionClock;

    public PasswordTagService() : this(new EphemeralSyncVersionClockService()) { }

    public PasswordTagService(ISyncVersionClockService versionClock)
    {
        _versionClock = versionClock;
    }

    public IReadOnlyList<PasswordTagInfoResponse> ConvertToPasswordTagInfoResponses(UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        return passwords.Tags
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag.Id)
            .Select(EndpointResponseMapper.ToPasswordTagInfoResponse)
            .ToList();
    }


    public void AddPasswordTag(NewPasswordTagRequest request, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        if (passwords.Tags.Count >= MaxNumberOfPasswordTags)
            throw new LimitReachedException(MaxNumberOfPasswordTags, "passwordTag");

        var tagName = NormalizeTagName(request.Name);
        var color = NormalizeColorCode(request.Color);
        ThrowIfPasswordTagNameExists(tagName, passwords);

        var tag = new PasswordTag
        {
            Id = Guid.NewGuid(),
            Name = tagName,
            Color = color,
            LastUpdatedAt = DateTime.UtcNow,
            Version = _versionClock.Next()
        };
        tag.GenerateIntegrityHash();

        passwords.DeletedTags.RemoveAll(deleted => deleted.Id == tag.Id);
        passwords.Tags.Add(tag);
        passwords.GeneratePasswordTagsIntegrityHash();
    }


    public void DeletePasswordTag(Guid passwordTagId, UserPasswordsData passwords)
    {
        using var tag = GetAndVerifyPasswordTagById(passwordTagId, passwords);
        TombstoneCleanupUtil.AddOrUpdateDeletedPasswordTag(passwords, tag.Id, DateTime.UtcNow, _versionClock.Next());
        passwords.Tags.Remove(tag);

        passwords.GeneratePasswordTagsIntegrityHash();
    }


    public void ExportPasswordTags(
        IReadOnlyList<Guid> passwordTagIds,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords)
    {
        ArgumentNullException.ThrowIfNull(passwordTagIds);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();

        ValidatePasswordTagExportRequest(passwordTagIds, targetPasswords);

        var selectedTags = GetPasswordTagsToExport(passwordTagIds, sourcePasswords);
        EnsureTargetPasswordTagNamesAreAvailable(selectedTags, targetPasswords);

        var now = DateTime.UtcNow;
        var copiedTags = selectedTags
            .Select(tag =>
            {
                var copiedTag = new PasswordTag
                {
                    Id = Guid.NewGuid(),
                    Name = NormalizeTagName(tag.Name),
                    Color = NormalizeColorCode(tag.Color),
                    LastUpdatedAt = now,
                    Version = _versionClock.Next()
                };
                copiedTag.GenerateIntegrityHash();
                return copiedTag;
            })
            .ToList();

        try
        {
            foreach (var copiedTag in copiedTags)
            {
                targetPasswords.DeletedTags.RemoveAll(deleted => deleted.Id == copiedTag.Id);
                targetPasswords.Tags.Add(copiedTag);
            }

            targetPasswords.GeneratePasswordTagsIntegrityHash();
        }
        catch
        {
            var copiedIds = copiedTags.Select(tag => tag.Id).ToHashSet();
            targetPasswords.Tags.RemoveAll(tag => copiedIds.Contains(tag.Id));
            copiedTags.ForEach(tag => tag.Dispose());
            throw;
        }
    }


    public void UpdatePasswordTag(UpdatePasswordTagRequest request, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var tag = GetAndVerifyPasswordTagById(request.Id, passwords);

        if (request.Name is not null)
        {
            var normalizedName = NormalizeTagName(request.Name);
            ThrowIfPasswordTagNameExists(normalizedName, passwords, tag.Id);
            tag.Name = normalizedName;
        }

        if (request.Color is not null)
            tag.Color = NormalizeColorCode(request.Color);

        passwords.DeletedTags.RemoveAll(deleted => deleted.Id == tag.Id);
        tag.LastUpdatedAt = DateTime.UtcNow;
        tag.Version = _versionClock.Next();
        tag.GenerateIntegrityHash();
        passwords.GeneratePasswordTagsIntegrityHash();
    }


    public PasswordTag GetAndVerifyPasswordTagById(Guid passwordTagId, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        var tag = passwords.Tags.FirstOrDefault(tag => tag.Id == passwordTagId);
        if (tag is null)
            throw new PasswordTagNotFoundException(passwordTagId);

        tag.VerifyIntegrity();
        return tag;
    }


    private void ValidatePasswordTagExportRequest(
        IReadOnlyList<Guid> passwordTagIds,
        UserPasswordsData targetPasswords)
    {
        var hasInvalidInput = passwordTagIds.Count == 0
            || passwordTagIds.Count > MaxNumberOfPasswordTags
            || passwordTagIds.Any(id => id == Guid.Empty)
            || passwordTagIds.Distinct().Count() != passwordTagIds.Count;

        if (hasInvalidInput)
            throw new InvalidInputException(["PasswordTagIds"]);

        if (targetPasswords.Tags.Count + passwordTagIds.Count > MaxNumberOfPasswordTags)
            throw new LimitReachedException(MaxNumberOfPasswordTags, "passwordTag");
    }


    private List<PasswordTag> GetPasswordTagsToExport(
        IReadOnlyList<Guid> passwordTagIds,
        UserPasswordsData sourcePasswords)
    {
        var selectedTags = new List<PasswordTag>(passwordTagIds.Count);
        foreach (var passwordTagId in passwordTagIds)
        {
            var tag = sourcePasswords.Tags.FirstOrDefault(tag => tag.Id == passwordTagId);
            if (tag is null)
                throw new PasswordTagNotFoundException(passwordTagId);

            tag.VerifyIntegrity();
            selectedTags.Add(tag);
        }

        return selectedTags;
    }


    private void EnsureTargetPasswordTagNamesAreAvailable(
        IReadOnlyList<PasswordTag> selectedTags,
        UserPasswordsData targetPasswords)
    {
        var usedNames = targetPasswords.Tags
            .Select(tag => NormalizeTagName(tag.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in selectedTags)
        {
            var tagName = NormalizeTagName(tag.Name);
            if (!usedNames.Add(tagName))
                throw new DuplicatePasswordTagNameException(tagName);
        }
    }


    private string NormalizeTagName(string name) => name.Trim();


    private string NormalizeColorCode(string colorCode) => colorCode.Trim().ToUpperInvariant();


    private void ThrowIfPasswordTagNameExists(
        string tagName,
        UserPasswordsData passwords,
        Guid? ignoredPasswordTagId = null)
    {
        var normalizedName = NormalizeTagName(tagName);
        var exists = passwords.Tags.Any(tag =>
            (!ignoredPasswordTagId.HasValue || tag.Id != ignoredPasswordTagId.Value)
            && string.Equals(NormalizeTagName(tag.Name), normalizedName, StringComparison.OrdinalIgnoreCase));

        if (exists)
            throw new DuplicatePasswordTagNameException(normalizedName);
    }
}
