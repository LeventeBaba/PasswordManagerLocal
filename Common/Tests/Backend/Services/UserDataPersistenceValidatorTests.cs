using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataPersistenceValidatorTests
{
    [TestMethod]
    public async Task EnsureUserDataBundleCanBePersisted_DuplicateDeviceNames_AreAllowedForDerivedPresentationResolution()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var validator = host.Services.GetRequiredService<IUserDataPersistenceValidator>();
        var versionClock = host.Services.GetRequiredService<ISyncVersionClockService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("validator_user"));
        var user = await lookup.GetAndVerifyUserAsync(token);
        var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token, user: user);
        var existing = bundle.UserDevicesData.Devices.Single();
        bundle.UserDevicesData.Devices.Add(new UserDeviceData
        {
            Id = Guid.NewGuid(),
            Name = existing.Name,
            LinkedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            Version = versionClock.Next()
        });

        validator.EnsureUserDataBundleCanBePersisted(bundle, user);
    }

    [TestMethod]
    public async Task EnsureUserDataBundleCanBePersisted_ConcurrentDuplicateTagAndColorFields_AreAllowed()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var validator = host.Services.GetRequiredService<IUserDataPersistenceValidator>();
        var versionClock = host.Services.GetRequiredService<ISyncVersionClockService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("validator_duplicate_fields"));
        var user = await lookup.GetAndVerifyUserAsync(token);
        var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token, user: user);
        bundle.UserPasswordsData.Tags.AddRange([
            new PasswordTag { Id = Guid.NewGuid(), Name = "Shared", Color = "#FF14B8A6", Version = versionClock.Next() },
            new PasswordTag { Id = Guid.NewGuid(), Name = "Shared", Color = "#FF14B8A6", Version = versionClock.Next() }
        ]);
        bundle.UserPasswordsData.CustomColors.AddRange([
            new CustomUserColor { Id = Guid.NewGuid(), ColorName = "Shared", ColorCode = "#FF123456", Version = versionClock.Next() },
            new CustomUserColor { Id = Guid.NewGuid(), ColorName = "Shared", ColorCode = "#FF123456", Version = versionClock.Next() }
        ]);

        validator.EnsureUserDataBundleCanBePersisted(bundle, user);
    }

}
