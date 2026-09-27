using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public static class EndpointRpcTestData
{
    public static readonly Guid Token = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherToken = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid OtherItemId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static byte[] Password() => Enumerable.Repeat((byte)0x41, 16).ToArray();

    public static object CreateRequest(EndpointOperationId operationId) => operationId switch
    {
        EndpointOperationId.Register => new RegisterEndpointRequest
        {
            Request = new RegistrationRequest
            {
                Username = "ValidUser1",
                Password = Password(),
                FirstName = "Levente",
                LastName = "Baba",
                Email = "valid@example.test",
                RememberMe = true
            }
        },
        EndpointOperationId.Login => new LoginEndpointRequest
        {
            Request = new LoginRequest
            {
                Username = "ValidUser1",
                Password = Password(),
                RememberMe = true
            }
        },
        EndpointOperationId.RenewAuthSession => new RenewAuthSessionEndpointRequest { Token = Token },
        EndpointOperationId.Logout => new LogoutEndpointRequest { Token = Token },
        EndpointOperationId.GetAuthSessionStatus => new GetAuthSessionStatusEndpointRequest { Token = Token },
        EndpointOperationId.ChangeMasterPassword => new ChangeMasterPasswordEndpointRequest
        {
            Request = new MasterPasswordChangeRequest
            {
                Token = Token,
                Password = Password(),
                NewPassword = Enumerable.Repeat((byte)0x42, 16).ToArray()
            }
        },
        EndpointOperationId.GetUserProfileInfo => new GetUserProfileInfoEndpointRequest { Token = Token },
        EndpointOperationId.DeleteUserAccount => new DeleteUserAccountEndpointRequest { Token = Token, Password = Password() },
        EndpointOperationId.ChangeUsername => new ChangeUsernameEndpointRequest { Token = Token, NewUsername = "ValidUser2" },
        EndpointOperationId.UpdateUserProfileInfo => new UpdateUserProfileInfoEndpointRequest
        {
            Request = new UpdateUserProfileRequest { Token = Token, NewEamil = "changed@example.test" }
        },
        EndpointOperationId.GetLocalDeviceInfo => new GetLocalDeviceInfoEndpointRequest(),
        EndpointOperationId.GetLocalUserSyncOn => new GetLocalUserSyncOnEndpointRequest { Token = Token },
        EndpointOperationId.SetLocalUserSyncOn => new SetLocalUserSyncOnEndpointRequest { Token = Token, IsSyncOn = true },
        EndpointOperationId.SetLocalDeviceName => new SetLocalDeviceNameEndpointRequest { Token = Token, Name = "Desktop" },
        EndpointOperationId.GetUserDevices => new GetUserDevicesEndpointRequest { Token = Token },
        EndpointOperationId.SetUserDeviceName => new SetUserDeviceNameEndpointRequest { Token = Token, DeviceId = ItemId, Name = "Laptop" },
        EndpointOperationId.SetUserDeviceSyncOn => new SetUserDeviceSyncOnEndpointRequest { Token = Token, DeviceId = ItemId, IsSyncOn = true },
        EndpointOperationId.UnblockUserDevice => new UnblockUserDeviceEndpointRequest { Token = Token, DeviceId = ItemId },
        EndpointOperationId.DisconnectUserDevice => new DisconnectUserDeviceEndpointRequest { Token = Token, DeviceId = ItemId, MasterPassword = Password() },
        EndpointOperationId.StartDeviceEnrollment => new StartDeviceEnrollmentEndpointRequest(),
        EndpointOperationId.GetDeviceEnrollmentStatus => new GetDeviceEnrollmentStatusEndpointRequest(),
        EndpointOperationId.CancelDeviceEnrollment => new CancelDeviceEnrollmentEndpointRequest(),
        EndpointOperationId.AddDeviceByCode => new AddDeviceByCodeEndpointRequest { Token = Token, Code = "ABCD2345" },
        EndpointOperationId.RestoreRememberedSessions => new RestoreRememberedSessionsEndpointRequest(),
        EndpointOperationId.InitializeRememberMeSession => new InitializeRememberMeSessionEndpointRequest { UserId = ItemId },
        EndpointOperationId.SetRememberMe => new SetRememberMeEndpointRequest { Token = Token, RememberMe = true },
        EndpointOperationId.GetSavedPasswords => new GetSavedPasswordsEndpointRequest { Token = Token },
        EndpointOperationId.AddNewPassword => new AddNewPasswordEndpointRequest
        {
            Token = Token,
            Request = new NewPasswordRequest
            {
                Name = "Example",
                Description = "Description",
                Color = "#FF14B8A6",
                Password = Password(),
                TagIds = []
            }
        },
        EndpointOperationId.RemovePasswords => new RemovePasswordsEndpointRequest { Token = Token, PasswordIds = [ItemId] },
        EndpointOperationId.GetUnsecurePassword => new GetUnsecurePasswordEndpointRequest { Token = Token, PasswordId = ItemId },
        EndpointOperationId.UpdatePassword => new UpdatePasswordEndpointRequest
        {
            Token = Token,
            Request = new UpdatePasswordRequest { Id = ItemId, Name = "Updated" }
        },
        EndpointOperationId.ExportPasswordsToUser => new ExportPasswordsToUserEndpointRequest
        {
            SourceToken = Token,
            Request = new ExportPasswordsToUserRequest { TargetToken = OtherToken, PasswordIds = [ItemId], DeleteOriginal = false }
        },
        EndpointOperationId.AddCustomUserColors => new AddCustomUserColorsEndpointRequest
        {
            Token = Token,
            Requests = [new NewCustomUserColorRequest { ColorName = "Primary", ColorCode = "#FF123456" }]
        },
        EndpointOperationId.DeleteCustomUserColors => new DeleteCustomUserColorsEndpointRequest { Token = Token, CustomUserColorIds = [ItemId] },
        EndpointOperationId.ExportCustomUserColorsToUser => new ExportCustomUserColorsToUserEndpointRequest
        {
            SourceToken = Token,
            Request = new ExportCustomUserColorsToUserRequest { TargetToken = OtherToken, CustomUserColorIds = [ItemId], DeleteOriginal = false }
        },
        EndpointOperationId.UpdateCustomUserColor => new UpdateCustomUserColorEndpointRequest
        {
            Token = Token,
            Request = new UpdateCustomUserColorRequest { Id = ItemId, ColorCode = "#FF654321" }
        },
        EndpointOperationId.AddPasswordTag => new AddPasswordTagEndpointRequest
        {
            Token = Token,
            Request = new NewPasswordTagRequest { Name = "Work", Color = "#FF14B8A6" }
        },
        EndpointOperationId.DeletePasswordTag => new DeletePasswordTagEndpointRequest { Token = Token, PasswordTagId = ItemId },
        EndpointOperationId.ExportPasswordTagsToUser => new ExportPasswordTagsToUserEndpointRequest
        {
            SourceToken = Token,
            Request = new ExportPasswordTagsToUserRequest { TargetToken = OtherToken, PasswordTagIds = [ItemId], DeleteOriginal = false }
        },
        EndpointOperationId.UpdatePasswordTag => new UpdatePasswordTagEndpointRequest
        {
            Token = Token,
            Request = new UpdatePasswordTagRequest { Id = ItemId, Name = "Personal" }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(operationId))
    };
}
