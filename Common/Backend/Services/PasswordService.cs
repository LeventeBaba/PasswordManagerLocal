using PasswordManagerLocal.Common.Backend.Mapping;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;
using PasswordManagerLocal.Common.Backend.Utils;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class PasswordService : IPasswordService
{
    private readonly ISyncVersionClockService _versionClock;

    public PasswordService() : this(new EphemeralSyncVersionClockService()) { }

    public PasswordService(ISyncVersionClockService versionClock)
    {
        _versionClock = versionClock;
    }

    public IReadOnlyList<PasswordInfoResponse> ConvertToPasswordInfoResponses(UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        var validTagIds = passwords.Tags.Select(tag => tag.Id).ToHashSet();
        return passwords.Passwords
            .OrderBy(password => password.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(password => password.Id)
            .Select(password =>
            {
                var response = EndpointResponseMapper.ToPasswordInfoResponse(password);
                response.TagIds = response.TagIds.Where(validTagIds.Contains).Distinct().Order().ToList();
                return response;
            })
            .ToList();
    }


    public async Task AddNewPassword(NewPasswordRequest request, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        if (passwords.Passwords.Count >= MaxNumberOfPasswords)
            throw new LimitReachedException(MaxNumberOfPasswords, "password");

        var normalizedName = NormalizePasswordName(request.Name);
        ThrowIfPasswordNameExists(normalizedName, passwords);

        EnsureTagIdsExist(request.TagIds, passwords);

        var now = DateTime.UtcNow;
        var securePassword = new SecurePassword
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Description = request.Description,
            Color = NormalizeColorCode(request.Color),
            Password = await EncryptPasswordAsync(request.Password, passwords),
            TagIds = NormalizeTagIds(request.TagIds),
            CreatedAt = now,
            LastUpdatedAt = now,
            Version = _versionClock.Next()
        };
        securePassword.GenerateIntegrityHash();

        passwords.DeletedPasswords.RemoveAll(deleted => deleted.Id == securePassword.Id);
        passwords.Passwords.Add(securePassword);
        passwords.GeneratePasswordsIntegrityHash();
    }


    public void RemovePasswords(IReadOnlyList<Guid> passwordIds, UserPasswordsData passwords)
    {
        ArgumentNullException.ThrowIfNull(passwordIds);
        passwords.VerifyIntegrity();

        if (passwordIds.Count == 0)
            return;

        var uniquePasswordIds = passwordIds.Distinct().ToList();
        var passwordsToRemove = new List<SecurePassword>(uniquePasswordIds.Count);

        foreach (var passwordId in uniquePasswordIds)
        {
            var password = passwords.Passwords.FirstOrDefault(password => password.Id == passwordId);
            if (password is null)
                throw new PasswordNotFoundException(passwordId);

            password.VerifyIntegrity();
            passwordsToRemove.Add(password);
        }

        foreach (var password in passwordsToRemove)
        {
            TombstoneCleanupUtil.AddOrUpdateDeletedPassword(passwords, password.Id, DateTime.UtcNow, _versionClock.Next());
            passwords.Passwords.Remove(password);
            password.Dispose();
        }

        passwords.GeneratePasswordsIntegrityHash();
    }


    public SecurePassword GetAndVerifyPasswordById(Guid passwordId, UserPasswordsData passwords)
    {
        passwords.VerifyIntegrity();

        var password = passwords.Passwords.FirstOrDefault(pw => pw.Id == passwordId);
        if (password is null)
            throw new PasswordNotFoundException(passwordId);

        password.VerifyIntegrity();
        return password;
    }


    public async Task<byte[]> GetUnsecurePasswordAsync(Guid passwordId, UserPasswordsData passwords)
    {
        var password = GetAndVerifyPasswordById(passwordId, passwords);
        return await DecryptPasswordAsync(password.Password, passwords);
    }


    public async Task UpdatePasswordAsync(UpdatePasswordRequest request, UserPasswordsData passwords)
    {
        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var password = GetAndVerifyPasswordById(request.Id, passwords);

        if (request.Name is not null)
        {
            var normalizedName = NormalizePasswordName(request.Name);
            ThrowIfPasswordNameExists(normalizedName, passwords, password.Id);
            password.Name = normalizedName;
        }

        if (request.Description is not null)
            password.Description = request.Description;

        if (request.Color is not null)
            password.Color = NormalizeColorCode(request.Color);

        if (request.Password is not null)
        {
            CryptographicOperations.ZeroMemory(password.Password);
            password.Password = await EncryptPasswordAsync(request.Password, passwords);
        }

        if (request.TagIds is not null)
        {
            EnsureTagIdsExist(request.TagIds, passwords);
            password.TagIds = NormalizeTagIds(request.TagIds);
        }

        passwords.DeletedPasswords.RemoveAll(deleted => deleted.Id == password.Id);
        password.LastUpdatedAt = DateTime.UtcNow;
        password.Version = _versionClock.Next();
        password.GenerateIntegrityHash();
        passwords.GeneratePasswordsIntegrityHash();
    }


    public async Task ExportPasswordsAsync(
        IReadOnlyList<Guid> passwordIds,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords)
    {
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();

        ValidatePasswordExportRequest(passwordIds, targetPasswords);

        var selectedPasswords = GetPasswordsToExport(passwordIds, sourcePasswords);
        EnsureTargetPasswordNamesAreAvailable(selectedPasswords, targetPasswords);

        var copiedPasswords = await CopyPasswordsForExportAsync(selectedPasswords, sourcePasswords, targetPasswords);
        try
        {
            AddCopiedPasswordsToTarget(copiedPasswords, targetPasswords);
            targetPasswords.GeneratePasswordsIntegrityHash();
        }
        catch
        {
            RollBackCopiedPasswords(copiedPasswords, targetPasswords);
            throw;
        }
    }


    private void ValidatePasswordExportRequest(IReadOnlyList<Guid> passwordIds, UserPasswordsData targetPasswords)
    {
        var hasInvalidInput = passwordIds.Count == 0
            || passwordIds.Count > MaxNumberOfPasswords
            || passwordIds.Any(id => id == Guid.Empty)
            || passwordIds.Distinct().Count() != passwordIds.Count;

        if (hasInvalidInput)
            throw new InvalidInputException(["PasswordIds"]);

        if (targetPasswords.Passwords.Count + passwordIds.Count > MaxNumberOfPasswords)
            throw new LimitReachedException(MaxNumberOfPasswords, "password");
    }


    private List<SecurePassword> GetPasswordsToExport(IReadOnlyList<Guid> passwordIds, UserPasswordsData sourcePasswords)
    {
        var selectedPasswords = new List<SecurePassword>(passwordIds.Count);
        foreach (var passwordId in passwordIds)
        {
            var password = sourcePasswords.Passwords.FirstOrDefault(pw => pw.Id == passwordId);
            if (password is null)
                throw new PasswordNotFoundException(passwordId);

            password.VerifyIntegrity();
            selectedPasswords.Add(password);
        }

        return selectedPasswords;
    }


    private void EnsureTargetPasswordNamesAreAvailable(
        IReadOnlyList<SecurePassword> selectedPasswords,
        UserPasswordsData targetPasswords)
    {
        var targetPasswordNames = new HashSet<string>(
            targetPasswords.Passwords.Select(password => NormalizePasswordName(password.Name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var password in selectedPasswords)
        {
            var normalizedName = NormalizePasswordName(password.Name);
            if (!targetPasswordNames.Add(normalizedName))
                throw new DuplicatePasswordNameException(normalizedName);
        }
    }


    private async Task<List<SecurePassword>> CopyPasswordsForExportAsync(
        IReadOnlyList<SecurePassword> selectedPasswords,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords)
    {
        var now = DateTime.UtcNow;
        var versions = selectedPasswords.Select(_ => _versionClock.Next()).ToArray();
        var maxConcurrency = Math.Min(MaxConcurrentPasswordExports, Math.Max(1, Environment.ProcessorCount));
        using var limiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var copyTasks = selectedPasswords
            .Select((sourcePassword, index) => CopyPasswordForExportWithLimitAsync(
                sourcePassword,
                sourcePasswords,
                targetPasswords,
                now,
                versions[index],
                limiter))
            .ToArray();

        try
        {
            var copiedPasswords = await Task.WhenAll(copyTasks);
            return copiedPasswords.ToList();
        }
        catch
        {
            foreach (var task in copyTasks)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result.Dispose();
            }

            throw;
        }
    }


    private async Task<SecurePassword> CopyPasswordForExportWithLimitAsync(
        SecurePassword sourcePassword,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords,
        DateTime now,
        SyncVersionStamp version,
        SemaphoreSlim limiter)
    {
        await limiter.WaitAsync();
        try
        {
            return await CopyPasswordForExportAsync(sourcePassword, sourcePasswords, targetPasswords, now, version);
        }
        finally
        {
            limiter.Release();
        }
    }


    private async Task<SecurePassword> CopyPasswordForExportAsync(
        SecurePassword sourcePassword,
        UserPasswordsData sourcePasswords,
        UserPasswordsData targetPasswords,
        DateTime now,
        SyncVersionStamp version)
    {
        byte[] rawPassword = [];
        try
        {
            rawPassword = await DecryptPasswordAsync(sourcePassword.Password, sourcePasswords);

            var copiedPassword = new SecurePassword
            {
                Id = Guid.NewGuid(),
                Name = NormalizePasswordName(sourcePassword.Name),
                Description = sourcePassword.Description,
                Color = NormalizeColorCode(sourcePassword.Color),
                Password = await EncryptPasswordAsync(rawPassword, targetPasswords),
                TagIds = [],
                CreatedAt = now,
                LastUpdatedAt = now,
                Version = version
            };
            copiedPassword.GenerateIntegrityHash();
            return copiedPassword;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawPassword);
        }
    }


    private void AddCopiedPasswordsToTarget(
        IReadOnlyList<SecurePassword> copiedPasswords,
        UserPasswordsData targetPasswords)
    {
        foreach (var copiedPassword in copiedPasswords)
        {
            targetPasswords.DeletedPasswords.RemoveAll(deleted => deleted.Id == copiedPassword.Id);
            targetPasswords.Passwords.Add(copiedPassword);
        }
    }


    private void RollBackCopiedPasswords(
        IReadOnlyList<SecurePassword> copiedPasswords,
        UserPasswordsData targetPasswords)
    {
        var copiedIds = copiedPasswords.Select(password => password.Id).ToHashSet();
        targetPasswords.Passwords.RemoveAll(password => copiedIds.Contains(password.Id));
        DisposeCopiedPasswords(copiedPasswords);
    }


    private void DisposeCopiedPasswords(IEnumerable<SecurePassword> copiedPasswords)
    {
        foreach (var copiedPassword in copiedPasswords)
            copiedPassword.Dispose();
    }


    private string NormalizePasswordName(string name) => name.Trim();


    private string NormalizeColorCode(string colorCode) => colorCode.Trim().ToUpperInvariant();


    private List<Guid> NormalizeTagIds(IReadOnlyList<Guid>? tagIds) =>
        tagIds is null ? [] : tagIds.Distinct().Order().ToList();


    private void EnsureTagIdsExist(IReadOnlyList<Guid>? tagIds, UserPasswordsData passwords)
    {
        if (tagIds is null || tagIds.Count == 0)
            return;

        var existingTagIds = passwords.Tags.Select(tag => tag.Id).ToHashSet();
        foreach (var tagId in tagIds)
        {
            if (!existingTagIds.Contains(tagId))
                throw new PasswordTagNotFoundException(tagId);
        }
    }


    private void ThrowIfPasswordNameExists(string name, UserPasswordsData passwords, Guid? ignoredPasswordId = null)
    {
        var exists = passwords.Passwords.Any(password =>
            (!ignoredPasswordId.HasValue || password.Id != ignoredPasswordId.Value)
            && string.Equals(NormalizePasswordName(password.Name), name, StringComparison.OrdinalIgnoreCase));

        if (exists)
            throw new DuplicatePasswordNameException(name);
    }


    public async Task<byte[]> EncryptPasswordAsync(byte[] raw, UserPasswordsData passwords)
    {
        using var key = EncryptionKey.FromRaw(passwords.PasswordKey);
        return await AES256.EncryptAsync(raw, key);
    }


    public async Task<byte[]> DecryptPasswordAsync(byte[] password, UserPasswordsData passwords)
    {
        using var key = EncryptionKey.FromRaw(passwords.PasswordKey);
        return await AES256.DecryptAsync(password, key);
    }
}
