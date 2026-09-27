using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceIdentityServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public void IdentityProperties_BeforeInitialization_Throw()
    {
        using var provider = CreateProvider(new FakeDeviceIdentityRepository(), new FakeUnitOfWork());
        var service = CreateService(provider);

        ExpectThrows<DeviceIdentityNotInitilaizedException>(() => _ = service.LocalDeviceId);
        ExpectThrows<DeviceIdentityNotInitilaizedException>(() => _ = service.AgreementPublicKey);
        ExpectThrows<DeviceIdentityNotInitilaizedException>(() => _ = service.SignPublicKey);
        ExpectThrows<DeviceIdentityNotInitilaizedException>(() => _ = service.Certificate);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Initialize_CreatesPersistedIdentity_AndSecondInstanceLoadsSameKeys()
    {
        var repository = new FakeDeviceIdentityRepository();
        var unitOfWork = new FakeUnitOfWork();
        using var provider = CreateProvider(repository, unitOfWork);
        var first = CreateService(provider);

        await first.InitializeAsync();

        MSTestAssert.IsTrue(first.IsInitialized);
        MSTestAssert.AreNotEqual(Guid.Empty, first.LocalDeviceId);
        MSTestAssert.AreEqual(32, first.AgreementPublicKey.Length);
        MSTestAssert.AreEqual(32, first.SignPublicKey.Length);
        MSTestAssert.IsFalse(string.IsNullOrWhiteSpace(first.FingerprintHex));
        MSTestAssert.AreEqual(DeviceType.WindowsPc, first.DeviceType);
        MSTestAssert.AreEqual(DeviceType.WindowsPc, repository.Snapshot()!.DeviceType);
        MSTestAssert.AreEqual(1, repository.CreateCalls);
        MSTestAssert.AreEqual(1, unitOfWork.SaveCalls);

        var second = CreateService(provider);
        await second.InitializeAsync();

        MSTestAssert.AreEqual(first.LocalDeviceId, second.LocalDeviceId);
        CollectionAssert.AreEqual(first.AgreementPublicKey, second.AgreementPublicKey);
        CollectionAssert.AreEqual(first.SignPublicKey, second.SignPublicKey);
        MSTestAssert.AreEqual(first.FingerprintHex, second.FingerprintHex);
        MSTestAssert.AreEqual(DeviceType.WindowsPc, second.DeviceType);
        MSTestAssert.AreEqual(1, repository.CreateCalls);
        MSTestAssert.AreEqual(1, unitOfWork.SaveCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EncryptForDevice_RecipientDecrypts_AndChangedAssociatedDataIsRejected()
    {
        using var senderProvider = CreateProvider(new FakeDeviceIdentityRepository(), new FakeUnitOfWork());
        using var recipientProvider = CreateProvider(new FakeDeviceIdentityRepository(), new FakeUnitOfWork());
        var sender = CreateService(senderProvider);
        var recipient = CreateService(recipientProvider);
        await sender.InitializeAsync();
        await recipient.InitializeAsync();

        var plaintext = "sensitive sync payload"u8.ToArray();
        var associatedData = "delta-metadata"u8.ToArray();
        var ciphertext = sender.EncryptForDevice(
            plaintext,
            recipient.AgreementPublicKey,
            associatedData,
            out var ephemeralPublicKey,
            out var nonce,
            out var tag);

        var decrypted = recipient.DecryptFromDevice(ciphertext, ephemeralPublicKey, nonce, tag, associatedData);

        CollectionAssert.AreEqual(plaintext, decrypted);
        MSTestAssert.IsFalse(ciphertext.SequenceEqual(plaintext));

        ExpectThrows<CryptographicException>(() =>
            recipient.DecryptFromDevice(ciphertext, ephemeralPublicKey, nonce, tag, "different"u8.ToArray()));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Initialize_UnsupportedDeviceType_DoesNotPersistInvalidIdentity()
    {
        var repository = new FakeDeviceIdentityRepository();
        var unitOfWork = new FakeUnitOfWork();
        using var provider = CreateProvider(repository, unitOfWork);
        var service = CreateService(provider, DeviceType.Unknown);

        await ExpectThrowsAsync<PlatformNotSupportedException>(() => service.InitializeAsync());

        MSTestAssert.IsFalse(service.IsInitialized);
        MSTestAssert.AreEqual(0, repository.CreateCalls);
        MSTestAssert.AreEqual(0, unitOfWork.SaveCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SetSyncOn_PersistsOnlyWhenValueChanges()
    {
        var repository = new FakeDeviceIdentityRepository();
        var unitOfWork = new FakeUnitOfWork();
        using var provider = CreateProvider(repository, unitOfWork);
        var service = CreateService(provider);
        await service.InitializeAsync();

        await service.SetSyncOnAsync(true);
        await service.SetSyncOnAsync(true);

        MSTestAssert.IsTrue(service.IsSyncOn);
        MSTestAssert.AreEqual(1, repository.UpdateCalls);
        MSTestAssert.AreEqual(2, unitOfWork.SaveCalls);
        MSTestAssert.IsTrue(repository.Snapshot()?.IsSyncOn ?? false);
    }

    private static ServiceProvider CreateProvider(
        FakeDeviceIdentityRepository repository,
        FakeUnitOfWork unitOfWork)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceIdentityRepository>(repository);
        services.AddSingleton<IUnitOfWork>(unitOfWork);
        services.AddSingleton<IKeyProtector, TestKeyProtector>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static DeviceIdentityService CreateService(
        IServiceProvider provider,
        DeviceType deviceType = DeviceType.WindowsPc) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            () => deviceType);

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
