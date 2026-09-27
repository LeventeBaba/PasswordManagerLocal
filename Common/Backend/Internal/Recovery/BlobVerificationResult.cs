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

internal sealed record BlobVerificationResult(bool Success, IDisposable? Value, UserDataBundleVerificationResult? Failure)
{
    public static BlobVerificationResult Verified(IDisposable value) => new(true, value, null);
    public static BlobVerificationResult Failed(UserDataBundleVerificationResult failure) => new(false, null, failure);
}
