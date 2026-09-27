using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class TokenServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Issue_ThenValidate_AndUidLookup_IsTrue()
    {
        ITokenService svc = new TokenService();

        var uid = Guid.NewGuid();
        var token = svc.Issue(uid);

        MSTestAssert.IsTrue(svc.Validate(token));

        MSTestAssert.IsTrue(svc.TryGetUid(token, out var uid2));
        MSTestAssert.AreEqual(uid, uid2);

        MSTestAssert.AreEqual(uid, svc.GetUidOrThrow(token));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Revoke_RemovesToken()
    {
        ITokenService svc = new TokenService();

        var uid = Guid.NewGuid();
        var token = svc.Issue(uid);

        MSTestAssert.IsTrue(svc.Validate(token));
        MSTestAssert.IsTrue(svc.Revoke(token));
        MSTestAssert.IsFalse(svc.Validate(token));
        MSTestAssert.IsFalse(svc.TryGetUid(token, out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Validate_EmptyOrUnknownToken_IsFalse()
    {
        ITokenService svc = new TokenService();

        MSTestAssert.IsFalse(svc.Validate(Guid.Empty));
        MSTestAssert.IsFalse(svc.Validate(Guid.NewGuid()));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryGetUid_EmptyOrUnknownToken_IsFalse()
    {
        ITokenService svc = new TokenService();

        MSTestAssert.IsFalse(svc.TryGetUid(Guid.Empty, out var uid1));
        MSTestAssert.AreEqual(Guid.Empty, uid1);

        MSTestAssert.IsFalse(svc.TryGetUid(Guid.NewGuid(), out var uid2));
        MSTestAssert.AreEqual(Guid.Empty, uid2);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Revoke_EmptyOrUnknownToken_IsFalse()
    {
        ITokenService svc = new TokenService();

        MSTestAssert.IsFalse(svc.Revoke(Guid.Empty));
        MSTestAssert.IsFalse(svc.Revoke(Guid.NewGuid()));
    }
}