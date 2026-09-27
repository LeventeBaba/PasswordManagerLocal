using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using System.Buffers.Binary;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class NetDeltaSigner
{
    public static byte[] CanonicalBytes(NetworkDelta d)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("PasswordManagerLocal.Common.Backend.SyncDelta.Signature.v2");
        SyncCryptoUtil.WriteString(bw, d.Entity);
        SyncCryptoUtil.WriteBytes(bw, d.Payload);
        bw.Write(d.Ts);
        SyncCryptoUtil.WriteString(bw, d.DeviceId);
        SyncCryptoUtil.WriteBytes(bw, d.SignPub);
        SyncCryptoUtil.WriteString(bw, d.RecipientDeviceId);
        bw.Write(d.EncryptionVersion);
        SyncCryptoUtil.WriteBytes(bw, d.EphemeralPublicKey);
        SyncCryptoUtil.WriteBytes(bw, d.Nonce);
        SyncCryptoUtil.WriteBytes(bw, d.Tag);
        SyncCryptoUtil.WriteBytes(bw, d.PayloadHash);

        return ms.ToArray();
    }


    public static void FillSignature(NetworkDelta d, IDeviceIdentityService identity)
    {
        var data = CanonicalBytes(d);
        var sig = identity.Sign(data);
        d.Sig = sig;
    }


    public static bool VerifySignature(NetworkDelta d)
    {
        if (d.SignPub.Length == 0 || d.Sig.Length == 0)
            return false;

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, d.SignPub, KeyBlobFormat.RawPublicKey);
        return SignatureAlgorithm.Ed25519.Verify(publicKey, CanonicalBytes(d), d.Sig);
    }
}
