using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Windows.EndpointRpc.Security;

public static class EndpointSensitiveData
{
    public static RegistrationRequest Clone(RegistrationRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RegistrationRequest
        {
            Username = value.Username,
            Password = value.Password?.ToArray() ?? [],
            FirstName = value.FirstName,
            LastName = value.LastName,
            Email = value.Email,
            RememberMe = value.RememberMe
        };
    }

    public static LoginRequest Clone(LoginRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LoginRequest
        {
            Username = value.Username,
            Password = value.Password?.ToArray() ?? [],
            RememberMe = value.RememberMe
        };
    }

    public static MasterPasswordChangeRequest Clone(MasterPasswordChangeRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MasterPasswordChangeRequest
        {
            Token = value.Token,
            Password = value.Password?.ToArray() ?? [],
            NewPassword = value.NewPassword?.ToArray() ?? []
        };
    }

    public static NewPasswordRequest Clone(NewPasswordRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new NewPasswordRequest
        {
            Name = value.Name,
            Description = value.Description,
            Color = value.Color,
            Password = value.Password?.ToArray() ?? [],
            TagIds = value.TagIds?.ToList()
        };
    }

    public static UpdatePasswordRequest Clone(UpdatePasswordRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new UpdatePasswordRequest
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            Color = value.Color,
            Password = value.Password?.ToArray(),
            TagIds = value.TagIds?.ToList()
        };
    }

    public static void ClearRequest(EndpointOperationId operationId, object value)
    {
        switch (operationId)
        {
            case EndpointOperationId.Register when value is RegisterEndpointRequest request:
                Clear(request.Request?.Password);
                break;
            case EndpointOperationId.Login when value is LoginEndpointRequest request:
                Clear(request.Request?.Password);
                break;
            case EndpointOperationId.ChangeMasterPassword when value is ChangeMasterPasswordEndpointRequest request:
                Clear(request.Request?.Password);
                Clear(request.Request?.NewPassword);
                break;
            case EndpointOperationId.DeleteUserAccount when value is DeleteUserAccountEndpointRequest request:
                Clear(request.Password);
                break;
            case EndpointOperationId.DisconnectUserDevice when value is DisconnectUserDeviceEndpointRequest request:
                Clear(request.MasterPassword);
                break;
            case EndpointOperationId.AddNewPassword when value is AddNewPasswordEndpointRequest request:
                Clear(request.Request?.Password);
                break;
            case EndpointOperationId.UpdatePassword when value is UpdatePasswordEndpointRequest request:
                Clear(request.Request?.Password);
                break;
        }
    }

    public static void ClearResponse(EndpointOperationId operationId, object value)
    {
        if (operationId == EndpointOperationId.GetUnsecurePassword &&
            value is GetUnsecurePasswordEndpointResponse response)
        {
            Clear(response.Password);
        }
    }

    public static void Clear(byte[]? value)
    {
        if (value is not null && value.Length > 0)
            CryptographicOperations.ZeroMemory(value);
    }
}
