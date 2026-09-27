using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserPasswordsService : IUserPasswordsService
{
    private readonly IUserSessionService _sessions;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly IPasswordService _passwordService;
    private readonly ICustomUserColorService _customUserColorService;
    private readonly IPasswordTagService _passwordTagService;

    public UserPasswordsService(
        IUserSessionService sessions,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        IPasswordService passwordService,
        ICustomUserColorService customUserColorService,
        IPasswordTagService passwordTagService)
    {
        _sessions = sessions;
        _reader = reader;
        _writer = writer;
        _passwordService = passwordService;
        _customUserColorService = customUserColorService;
        _passwordTagService = passwordTagService;
    }



    public async Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        return new SavedPasswordsResponse
        {
            Passwords = _passwordService.ConvertToPasswordInfoResponses(bundle.UserPasswordsData),
            CustomColors = _customUserColorService.ConvertToCustomUserColorInfoResponses(bundle.UserPasswordsData),
            Tags = _passwordTagService.ConvertToPasswordTagInfoResponses(bundle.UserPasswordsData)
        };
    }


    public async Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        await _passwordService.AddNewPassword(request, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }


    public async Task RemovePasswordsAsync(
        Guid token,
        IReadOnlyList<Guid> passwordIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passwordIds);

        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);

        if (passwordIds.Count == 0)
            return;

        _passwordService.RemovePasswords(passwordIds, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }


    public async Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        return await _passwordService.GetUnsecurePasswordAsync(passwordId, bundle.UserPasswordsData);
    }


    public async Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        await _passwordService.UpdatePasswordAsync(request, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }

    public async Task ExportPasswordsToUserAsync(
        Guid sourceToken,
        ExportPasswordsToUserRequest request,
        CancellationToken ct = default)
    {
        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var sourceUid = _sessions.GetUidFromToken(sourceToken);
        var targetUid = _sessions.GetUidFromToken(request.TargetToken);
        if (sourceUid == targetUid)
            throw new InvalidInputException(["TargetToken"]);

        var sourceBundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(sourceToken, ct);
        var targetBundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(request.TargetToken, ct);

        await _passwordService.ExportPasswordsAsync(
            request.PasswordIds!,
            sourceBundle.UserPasswordsData,
            targetBundle.UserPasswordsData);

        await _writer.UpdateUserDataBundleAsync(
            targetBundle,
            request.TargetToken,
            UserDataBlobKind.Passwords,
            true,
            ct);

        if (!request.DeleteOriginal)
            return;

        try
        {
            _passwordService.RemovePasswords(request.PasswordIds!, sourceBundle.UserPasswordsData);
            await _writer.UpdateUserDataBundleAsync(
                sourceBundle,
                sourceToken,
                UserDataBlobKind.Passwords,
                true,
                ct);
        }
        catch (MutationPartiallyCommittedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MutationPartiallyCommittedException(
                "The password export was committed to the target profile, but source cleanup did not complete.",
                innerException: ex);
        }
    }

}
