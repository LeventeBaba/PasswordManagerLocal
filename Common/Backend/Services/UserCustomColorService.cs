using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserCustomColorService : IUserCustomColorService
{
    private readonly IUserSessionService _sessions;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly ICustomUserColorService _customUserColorService;

    public UserCustomColorService(
        IUserSessionService sessions,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        ICustomUserColorService customUserColorService)
    {
        _sessions = sessions;
        _reader = reader;
        _writer = writer;
        _customUserColorService = customUserColorService;
    }


    public Task AddCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<NewCustomUserColorRequest> requests,
        CancellationToken ct = default)
    {
        return _writer.ExecuteMutationAsync(token, async ct =>
        {
            using var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
            _customUserColorService.AddCustomUserColors(requests, bundle.UserPasswordsData);
            await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);

        }, ct);
    }


    public Task DeleteCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<Guid> customUserColorIds,
        CancellationToken ct = default)
    {
        return _writer.ExecuteMutationAsync(token, async ct =>
        {
            ArgumentNullException.ThrowIfNull(customUserColorIds);

            using var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);

            if (customUserColorIds.Count == 0)
                return;

            _customUserColorService.DeleteCustomUserColors(customUserColorIds, bundle.UserPasswordsData);
            await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);

        }, ct);
    }


    public Task ExportCustomUserColorsToUserAsync(
        Guid sourceToken,
        ExportCustomUserColorsToUserRequest request,
        CancellationToken ct = default)
    {
        return _writer.ExecuteMutationsAsync(sourceToken, request.TargetToken, async ct =>
        {
            if (!request.Validate(out var errors))
                throw new InvalidInputException(errors);

            var sourceUid = _sessions.GetUidFromToken(sourceToken);
            var targetUid = _sessions.GetUidFromToken(request.TargetToken);
            if (sourceUid == targetUid)
                throw new InvalidInputException(["TargetToken"]);

            using var sourceBundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(sourceToken, ct);
            using var targetBundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(request.TargetToken, ct);

            _customUserColorService.ExportCustomUserColors(
                request.CustomUserColorIds!,
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
                _customUserColorService.DeleteCustomUserColors(
                    request.CustomUserColorIds!,
                    sourceBundle.UserPasswordsData);
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
                    "The custom-color export was committed to the target profile, but source cleanup did not complete.",
                    innerException: ex);
            }

        }, ct);
    }


    public Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default)
    {
        return _writer.ExecuteMutationAsync(token, async ct =>
        {
            using var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
            _customUserColorService.UpdateCustomUserColor(request, bundle.UserPasswordsData);
            await _writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords, true, ct);

        }, ct);
    }
}
