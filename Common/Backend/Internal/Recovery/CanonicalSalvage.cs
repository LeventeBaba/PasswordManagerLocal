using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

internal sealed class CanonicalSalvage : IDisposable
{
    public CanonicalSalvage(
        UserData root,
        GeneralUserData? general,
        UserPasswordsData? passwords,
        UserDevicesData? devices,
        UserDataBlobKind failedComponents,
        DateTimeOffset lastModifiedAt)
    {
        Root = root;
        General = general;
        Passwords = passwords;
        Devices = devices;
        FailedComponents = failedComponents;
        LastModifiedAt = lastModifiedAt;
    }

    public UserData Root { get; }
    public GeneralUserData? General { get; }
    public UserPasswordsData? Passwords { get; }
    public UserDevicesData? Devices { get; }
    public UserDataBlobKind FailedComponents { get; }
    public DateTimeOffset LastModifiedAt { get; }

    public void Dispose()
    {
        Root.Dispose();
        General?.Dispose();
        Passwords?.Dispose();
        Devices?.Dispose();
    }
}
