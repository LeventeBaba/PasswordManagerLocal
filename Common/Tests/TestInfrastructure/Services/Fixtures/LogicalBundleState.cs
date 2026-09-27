using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.Backend.Services;
namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed class LogicalBundleState : IDisposable
{
    private LogicalBundleState(
        GeneralUserData general,
        UserPasswordsData passwords,
        UserDevicesData devices,
        User user,
        UserSyncPayload payload)
    {
        General = general;
        Passwords = passwords;
        Devices = devices;
        User = user;
        Payload = payload;
    }

    public GeneralUserData General { get; }
    public UserPasswordsData Passwords { get; }
    public UserDevicesData Devices { get; }
    public User User { get; }
    public UserSyncPayload Payload { get; }

    public static LogicalBundleState Create(
        GeneralUserData general,
        UserPasswordsData passwords,
        UserDevicesData devices,
        byte identityByte)
    {
        var userId = Guid.Parse("03000000-0000-0000-0000-000000000030");
        var usernameHash = Enumerable.Repeat(identityByte, 32).ToArray();
        var usernameSalt = Enumerable.Repeat((byte)(identityByte + 10), 16).ToArray();
        return new LogicalBundleState(
            general,
            passwords,
            devices,
            new User
            {
                UId = userId,
                UsernameHash = usernameHash.ToArray(),
                UsernameSalt = usernameSalt.ToArray()
            },
            new UserSyncPayload
            {
                UId = userId,
                UsernameHash = usernameHash,
                UsernameSalt = usernameSalt
            });
    }

    public LogicalBundleState Clone()
    {
        var general = DeterministicItemVersionMergeTests.General(
            General.Username,
            General.LastUpdatedAt,
            General.Version);
        general.FirstName = General.FirstName;
        general.LastName = General.LastName;
        general.Email = General.Email;
        general.RegistrationDate = General.RegistrationDate;
        general.RegistrationTimeZoneId = General.RegistrationTimeZoneId;
        general.RegistrationDeviceType = General.RegistrationDeviceType;
        general.GenerateIntegrityHash();
        return new LogicalBundleState(
            general,
            DeterministicItemVersionMergeTests.Clone(Passwords),
            DeterministicItemVersionMergeTests.Clone(Devices),
            new User
            {
                UId = User.UId,
                UsernameHash = User.UsernameHash.ToArray(),
                UsernameSalt = User.UsernameSalt.ToArray()
            },
            new UserSyncPayload
            {
                UId = Payload.UId,
                UsernameHash = Payload.UsernameHash.ToArray(),
                UsernameSalt = Payload.UsernameSalt.ToArray()
            });
    }

    public bool MergeFrom(LogicalBundleState incoming)
    {
        var changed = GeneralUserDataMergeUtil.Merge(General, incoming.General, User, incoming.Payload);
        changed |= new UserPasswordsDataMergeService().Merge(Passwords, incoming.Passwords);
        changed |= new UserDevicesDataMergeService().Merge(Devices, incoming.Devices);
        return changed;
    }

    public string Fingerprint() => string.Join(
        "|",
        Convert.ToHexString(General.CalculateIntegrityHash()),
        Convert.ToHexString(User.UsernameHash),
        Convert.ToHexString(User.UsernameSalt),
        Convert.ToHexString(Passwords.CalculateIntegrityHash()),
        Convert.ToHexString(Devices.CalculateIntegrityHash()));

    public void Dispose()
    {
        General.Dispose();
        Passwords.Dispose();
        Devices.Dispose();
    }
}
