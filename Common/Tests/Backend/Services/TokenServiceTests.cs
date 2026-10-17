using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Notifications;
using static PasswordManagerLocal.Common.Backend.Constants.TokenConstants;

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

    [TestMethod]
    [TestCategory("Backend")]
    public async Task ExpiredSession_PublishesOneUserScopedInvalidation()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        ITokenService svc = new TokenService(hub, clock);
        var userId = Guid.NewGuid();
        var token = svc.Issue(userId);
        await using var subscription = hub.Subscribe(userId, capacity: 4);

        clock.Advance(LoginTokenExpirationTime + TimeSpan.FromMilliseconds(1));
        MSTestAssert.AreEqual(1, svc.PurgeExpired());
        MSTestAssert.AreEqual(0, svc.PurgeExpired());
        MSTestAssert.IsFalse(svc.Validate(token));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BackendChangeDelivery? delivery = null;
        await foreach (var item in subscription.ReadAllAsync(timeout.Token))
        {
            delivery = item;
            break;
        }

        MSTestAssert.IsNotNull(delivery);
        var published = delivery!;
        MSTestAssert.AreEqual(FrontendChangeScope.AuthenticationSessions, published.Notification!.Scope);
        MSTestAssert.AreEqual(userId, published.Notification.UserId);
        MSTestAssert.AreEqual(1L, published.Sequence);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
