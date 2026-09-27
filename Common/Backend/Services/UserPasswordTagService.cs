using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserPasswordTagService : IUserPasswordTagService
{
    private readonly IUserSessionService _sessions;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly IPasswordTagService _passwordTagService;

    public UserPasswordTagService(
        IUserSessionService sessions,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        IPasswordTagService passwordTagService)
    {
        _sessions = sessions;
        _reader = reader;
        _writer = writer;
        _passwordTagService = passwordTagService;
    }


    public async Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        _passwordTagService.AddPasswordTag(request, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }


    public async Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        _passwordTagService.DeletePasswordTag(passwordTagId, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }


    public async Task ExportPasswordTagsToUserAsync(
        Guid sourceToken,
        ExportPasswordTagsToUserRequest request,
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

        _passwordTagService.ExportPasswordTags(
            request.PasswordTagIds!,
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
            foreach (var passwordTagId in request.PasswordTagIds!)
                _passwordTagService.DeletePasswordTag(passwordTagId, sourceBundle.UserPasswordsData);

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
                "The password-tag export was committed to the target profile, but source cleanup did not complete.",
                innerException: ex);
        }
    }


    public async Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default)
    {
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        _passwordTagService.UpdatePasswordTag(request, bundle.UserPasswordsData);
        await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);
    }
}
