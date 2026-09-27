using PasswordManagerLocal.Common.Backend.Mapping;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Backend.Services;

public class UserProfileService : IUserProfileService
{
    private readonly IUserLookupService _lookup;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly IUserSessionService _sessions;
    private readonly IUserDeletionService _deletion;
    private readonly ICredentialVerificationService _authService;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUserLifecycleCoordinator _lifecycle;

    public UserProfileService(
        IUserLookupService lookup,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        IUserSessionService sessions,
        IUserDeletionService deletion,
        ICredentialVerificationService authService,
        ISyncVersionClockService versionClock,
        IUserLifecycleCoordinator lifecycle)
    {
        _lookup = lookup;
        _reader = reader;
        _writer = writer;
        _sessions = sessions;
        _deletion = deletion;
        _authService = authService;
        _versionClock = versionClock;
        _lifecycle = lifecycle;
    }



    public async Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        return EndpointResponseMapper.ToUserProfileInfoResponse(
            bundle.UserData,
            bundle.GeneralUserData,
            user.SavedKey is not null);
    }


    public async Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default)
    {
        if (!IsValidPassword(password))
            throw new InvalidInputException();

        var user = await _lookup.GetAndVerifyUserAsync(token, ct);

        if (!_authService.IsPasswordValid(token, password, user.PasswordSalt))
            throw new InvalidInputException();

        await _deletion.DeleteUserAsync(user, true, ct);
    }


    public Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default)
    {
        if (!IsValidUsername(newUsername))
            throw new InvalidInputException();

        var userId = _sessions.GetUidFromToken(token);
        return _lifecycle.ExecuteAsync(
            userId,
            innerCt => ChangeUsernameUnderLifecycleAsync(token, userId, newUsername, innerCt),
            ct);
    }

    private async Task ChangeUsernameUnderLifecycleAsync(
        Guid token,
        Guid expectedUserId,
        string newUsername,
        CancellationToken ct)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        if (user.UId != expectedUserId)
            throw new InvalidTokenException();

        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        var usernameBytes = Encoding.UTF8.GetBytes(newUsername);

        try
        {
            var resolution = await _lookup.ResolveUsernameAsync(usernameBytes, ct);
            if (resolution.State == UserLoginIdentityMatchState.Matched && resolution.UserId != user.UId)
                throw new InvalidInputException();
            if (resolution.State is not UserLoginIdentityMatchState.NotFound and not UserLoginIdentityMatchState.Matched)
                throw new InvalidInputException();

            bundle.GeneralUserData.Username = newUsername;
            bundle.GeneralUserData.LastUpdatedAt = DateTime.UtcNow;
            bundle.GeneralUserData.Version = _versionClock.Next();

            CryptographicOperations.ZeroMemory(user.UsernameSalt);
            CryptographicOperations.ZeroMemory(user.UsernameHash);
            user.UsernameSalt = Hashing.GenerateSalt();
            user.UsernameHash = Hashing.SHA256Hash(usernameBytes, user.UsernameSalt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
        }

        using var key = _sessions.GetEncryptionKeyFromToken(token);
        await _writer.UpdateUserDataBundleAsync(bundle, user, key, UserDataBlobKind.General, true, ct);
    }


    public async Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default)
    {
        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var bundle = await _reader.GetLoadAndVerifyUserDataBundleAsync(request.Token, ct);

        if (request.NewEamil is not null)
            bundle.GeneralUserData.Email = request.NewEamil;

        if (request.newFirstName is not null)
            bundle.GeneralUserData.FirstName = request.newFirstName;

        if (request.NewLastName is not null)
            bundle.GeneralUserData.LastName = request.NewLastName;

        bundle.GeneralUserData.LastUpdatedAt = DateTime.UtcNow;
        bundle.GeneralUserData.Version = _versionClock.Next();

        await _writer.UpdateUserDataBundleAsync(bundle, request.Token, UserDataBlobKind.General, true, ct);
    }
}
