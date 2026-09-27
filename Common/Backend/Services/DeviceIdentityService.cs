using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceIdentityService : IDeviceIdentityService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DeviceType> _detectDeviceType;
    private Key? _ka = null;
    private Key? _sig = null;
    private X509Certificate2? _cert = null;
    private Guid _localDeviceId = Guid.Empty;
    private Guid _originInstanceId = Guid.Empty;
    private bool _isSyncOn;
    private DeviceType _deviceType;
    private DateTimeOffset _createdAt = DateTimeOffset.MinValue;
    private byte[]? _agreementPublicKey;
    private byte[]? _signPublicKey;
    private string? _deviceIdHex;
    private string? _fingerprintHex;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);


    public DeviceIdentityService(IServiceScopeFactory scopeFactory)
        : this(scopeFactory, DeviceTypeDetector.Detect)
    {
    }

    internal DeviceIdentityService(
        IServiceScopeFactory scopeFactory,
        Func<DeviceType> detectDeviceType)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(detectDeviceType);

        _scopeFactory = scopeFactory;
        _detectDeviceType = detectDeviceType;
    }



    public bool IsInitialized =>
        _ka is not null && _sig is not null && _cert is not null;


    public bool IsSyncOn =>
        _isSyncOn;


    public DeviceType DeviceType =>
        _deviceType;


    public DateTimeOffset CreatedAt =>
        _createdAt;


    public byte[] AgreementPublicKey
    {
        get
        {
            if (_ka is null)
                throw new DeviceIdentityNotInitilaizedException();

            _agreementPublicKey ??= _ka.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            return _agreementPublicKey.ToArray();
        }
    }

    public byte[] SignPublicKey
    {
        get
        {
            if (_sig is null)
                throw new DeviceIdentityNotInitilaizedException();

            _signPublicKey ??= _sig.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            return _signPublicKey.ToArray();
        }
    }

    public Guid LocalDeviceId
    {
        get
        {
            if (_localDeviceId == Guid.Empty)
                throw new DeviceIdentityNotInitilaizedException();

            return _localDeviceId;
        }
    }


    public Guid OriginInstanceId
    {
        get
        {
            if (_originInstanceId == Guid.Empty)
                throw new DeviceIdentityNotInitilaizedException();

            return _originInstanceId;
        }
    }


    public string DeviceIdHex
    {
        get
        {
            if (_sig is null)
                throw new DeviceIdentityNotInitilaizedException();

            if (_deviceIdHex is null)
            {
                _signPublicKey ??= _sig.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                _deviceIdHex = Convert.ToHexString(Hashing.SHA256Hash(_signPublicKey));
            }

            return _deviceIdHex;
        }
    }


    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        if (_sig is null)
            throw new DeviceIdentityNotInitilaizedException();

        return SignatureAlgorithm.Ed25519.Sign(_sig, data.ToArray());
    }


    public byte[] EncryptForDevice(byte[] plaintext, byte[] recipientAgreementPublicKey, byte[] associatedData, out byte[] ephemeralPublicKey, out byte[] nonce, out byte[] tag)
    {
        if (plaintext is null || plaintext.Length == 0)
            throw new InvalidDataException("Plaintext payload is empty.");

        if (recipientAgreementPublicKey is null || recipientAgreementPublicKey.Length != SyncDeltaX25519PublicKeyBytes)
            throw new InvalidDataException("Recipient agreement public key is invalid.");

        using var ephemeralKey = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters());
        var recipientPublicKey = NSec.Cryptography.PublicKey.Import(KeyAgreementAlgorithm.X25519, recipientAgreementPublicKey, KeyBlobFormat.RawPublicKey);
        using var sharedSecret = KeyAgreementAlgorithm.X25519.Agree(
            ephemeralKey,
            recipientPublicKey,
            new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport })
            ?? throw new CryptographicException("Key agreement failed.");

        ephemeralPublicKey = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        nonce = RandomNumberGenerator.GetBytes(SyncDeltaNonceBytes);
        tag = new byte[SyncDeltaTagBytes];

        var key = DeriveSyncAesKey(sharedSecret, ephemeralPublicKey, recipientAgreementPublicKey, associatedData);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, SyncDeltaTagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return ciphertext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }


    public byte[] DecryptFromDevice(byte[] ciphertext, byte[] senderEphemeralPublicKey, byte[] nonce, byte[] tag, byte[] associatedData)
    {
        if (_ka is null)
            throw new DeviceIdentityNotInitilaizedException();

        if (ciphertext is null || ciphertext.Length == 0)
            throw new InvalidDataException("Ciphertext payload is empty.");

        if (senderEphemeralPublicKey is null || senderEphemeralPublicKey.Length != SyncDeltaX25519PublicKeyBytes)
            throw new InvalidDataException("Sender ephemeral public key is invalid.");

        if (nonce is null || nonce.Length != SyncDeltaNonceBytes)
            throw new InvalidDataException("Delta nonce is invalid.");

        if (tag is null || tag.Length != SyncDeltaTagBytes)
            throw new InvalidDataException("Delta authentication tag is invalid.");

        var senderPublicKey = NSec.Cryptography.PublicKey.Import(KeyAgreementAlgorithm.X25519, senderEphemeralPublicKey, KeyBlobFormat.RawPublicKey);
        using var sharedSecret = KeyAgreementAlgorithm.X25519.Agree(
            _ka,
            senderPublicKey,
            new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport })
            ?? throw new CryptographicException("Key agreement failed.");

        _agreementPublicKey ??= _ka.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var key = DeriveSyncAesKey(sharedSecret, senderEphemeralPublicKey, _agreementPublicKey, associatedData);
        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, SyncDeltaTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }


    private byte[] DeriveSyncAesKey(SharedSecret sharedSecret, byte[] firstPublicKey, byte[] secondPublicKey, byte[] associatedData)
    {
        var rawSharedSecret = sharedSecret.Export(SharedSecretBlobFormat.RawSharedSecret);
        try
        {
            var salt = BuildKdfSalt(firstPublicKey, secondPublicKey);
            var info = BuildKdfInfo(firstPublicKey, secondPublicKey, associatedData);
            return HKDF.DeriveKey(HashAlgorithmName.SHA512, rawSharedSecret, 32, salt, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawSharedSecret);
        }
    }


    private byte[] BuildKdfSalt(byte[] firstPublicKey, byte[] secondPublicKey)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("PasswordManagerLocal.Common.Backend.SyncDelta.KdfSalt.v1");
        bw.Write(firstPublicKey.Length);
        bw.Write(firstPublicKey);
        bw.Write(secondPublicKey.Length);
        bw.Write(secondPublicKey);

        return Hashing.SHA256Hash(ms.ToArray());
    }


    private byte[] BuildKdfInfo(byte[] firstPublicKey, byte[] secondPublicKey, byte[] associatedData)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("PasswordManagerLocal.Common.Backend.SyncDelta.AES256GCM.v1");
        bw.Write(firstPublicKey.Length);
        bw.Write(firstPublicKey);
        bw.Write(secondPublicKey.Length);
        bw.Write(secondPublicKey);
        bw.Write(associatedData.Length);
        bw.Write(associatedData);

        return ms.ToArray();
    }


    public X509Certificate2 Certificate
    {
        get
        {
            if (_cert is null)
                throw new DeviceIdentityNotInitilaizedException();

            return _cert;
        }
    }


    public string FingerprintHex
    {
        get
        {
            if (_cert is null)
                throw new DeviceIdentityNotInitilaizedException();

            return _fingerprintHex ??= GetFingerprintHex(_cert);
        }
    }



    public string GetFingerprintHex(X509Certificate2 cert)
    {
        var hash = Hashing.SHA256Hash(cert.RawData);
        return Convert.ToHexString(hash);
    }







    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized)
            return;

        await _initializationLock.WaitAsync(ct);
        try
        {
            if (IsInitialized)
                return;

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceIdentityRepository>();
            var keyProtector = scope.ServiceProvider.GetRequiredService<IKeyProtector>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var identity = await repo.Get(ct);
            if (identity is null)
                await CreateIdentity(repo, keyProtector, uow, ct);
            else
            {
                identity.VerifyIntegrity();
                LoadIdentity(identity, keyProtector);
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }


    public async Task SetSyncOnAsync(bool isSyncOn, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceIdentityRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var identity = await repo.Get(ct);
        if (identity is null)
            throw new DeviceIdentityNotInitilaizedException();

        identity.VerifyIntegrity();

        if (identity.IsSyncOn == isSyncOn && _isSyncOn == isSyncOn)
            return;

        identity.IsSyncOn = isSyncOn;
        identity.GenerateIntegrityHash();
        repo.Update(identity);
        await uow.SaveChangesAsync(ct);

        _isSyncOn = isSyncOn;
    }

    private async Task CreateIdentity(IDeviceIdentityRepository repo, IKeyProtector keyProtector, IUnitOfWork uow, CancellationToken ct = default)
    {
        var deviceType = _detectDeviceType();
        if (!DeviceTypeDetector.IsValid(deviceType))
            throw new PlatformNotSupportedException("The current platform does not have a supported local device type.");

        _ka = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _sig = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var (cert, pfxBytes) = CreateCertificateAndPfx();
        _cert = cert;
        ResetCachedPublicIdentity();

        var rawKa = _ka.Export(KeyBlobFormat.RawPrivateKey);
        var rawSig = _sig.Export(KeyBlobFormat.RawPrivateKey);

        try
        {
            var protectedKa = keyProtector.Protect(rawKa);
            var protectedSig = keyProtector.Protect(rawSig);
            var protectedCert = keyProtector.Protect(pfxBytes);
            var newIdentity = new LocalDeviceIdentity
            {
                Id = Guid.NewGuid(),
                AgreementPrivateKeyBlob = protectedKa,
                SignPrivateKeyBlob = protectedSig,
                PFXCertificate = protectedCert,
                DeviceType = deviceType,
                IsSyncOn = false
            };
            _localDeviceId = newIdentity.Id;
            _originInstanceId = newIdentity.OriginInstanceId;
            _deviceType = newIdentity.DeviceType;
            _isSyncOn = false;
            _createdAt = newIdentity.CreatedAt;
            newIdentity.GenerateIntegrityHash();

            await repo.Create(newIdentity, ct);
            await uow.SaveChangesAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKa);
            CryptographicOperations.ZeroMemory(rawSig);
            CryptographicOperations.ZeroMemory(pfxBytes);
        }
    }


    private void LoadIdentity(LocalDeviceIdentity identity, IKeyProtector keyProtector)
    {
        identity.VerifyIntegrity();
        if (!DeviceTypeDetector.IsValid(identity.DeviceType))
            throw new InvalidDataException("The local device type is invalid.");

        _localDeviceId = identity.Id;
        _originInstanceId = identity.OriginInstanceId;
        _deviceType = identity.DeviceType;
        _isSyncOn = identity.IsSyncOn;
        _createdAt = identity.CreatedAt;

        var unprotectedKa = keyProtector.Unprotect(identity.AgreementPrivateKeyBlob);
        var unprotectedSig = keyProtector.Unprotect(identity.SignPrivateKeyBlob);
        var unprotectedCert = keyProtector.Unprotect(identity.PFXCertificate);

        try
        {
            _ka = Key.Import(KeyAgreementAlgorithm.X25519, unprotectedKa, KeyBlobFormat.RawPrivateKey);
            _sig = Key.Import(SignatureAlgorithm.Ed25519, unprotectedSig, KeyBlobFormat.RawPrivateKey);
            _cert = X509CertificateLoader.LoadPkcs12(unprotectedCert, PFXPassword, GetCertificateKeyStorageFlags(), Pkcs12LoaderLimits.Defaults);
            ResetCachedPublicIdentity();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unprotectedKa);
            CryptographicOperations.ZeroMemory(unprotectedSig);
            CryptographicOperations.ZeroMemory(unprotectedCert);
        }
    }




    private void ResetCachedPublicIdentity()
    {
        _agreementPublicKey = null;
        _signPublicKey = null;
        _deviceIdHex = null;
        _fingerprintHex = null;
    }

    private X509KeyStorageFlags GetCertificateKeyStorageFlags() =>
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet;



    private (X509Certificate2 Cert, byte[] PfxBytes) CreateCertificateAndPfx()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var req = new CertificateRequest("CN=PasswordManagerLocal Device", ecdsa, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        try
        {
            var san = BuildSan();
            req.CertificateExtensions.Add(san.Build());
        }
        catch { }

        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection {
            new Oid("1.3.6.1.5.5.7.3.1"),
            new Oid("1.3.6.1.5.5.7.3.2")
            }, false));

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(5);

        using var created = req.CreateSelfSigned(notBefore, notAfter);

        var pfx = created.Export(X509ContentType.Pfx, PFXPassword);

        var cert = X509CertificateLoader.LoadPkcs12(
            pfx,
            PFXPassword,
            GetCertificateKeyStorageFlags(),
            Pkcs12LoaderLimits.Defaults);

        return (cert, pfx);
    }




    private SubjectAlternativeNameBuilder BuildSan()
    {
        var b = new SubjectAlternativeNameBuilder();
        try
        {
            b.AddDnsName(Dns.GetHostName());
        }
        catch { }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipProps = ni.GetIPProperties();
                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        b.AddIpAddress(ip.Address);
                }
            }
        }
        catch { }

        return b;
    }
}