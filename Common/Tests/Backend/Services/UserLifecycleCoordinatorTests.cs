using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Services;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserLifecycleCoordinatorTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ExecuteAsync_IsReentrantWithinSameAsyncFlow()
    {
        var coordinator = new UserLifecycleCoordinator();
        var userId = Guid.NewGuid();
        var order = new List<int>();

        await coordinator.ExecuteAsync(userId, async outerToken =>
        {
            order.Add(1);
            await coordinator.ExecuteAsync(userId, innerToken =>
            {
                MSTestAssert.AreEqual(outerToken, innerToken);
                order.Add(2);
                return Task.CompletedTask;
            }, outerToken);
            order.Add(3);
        });

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ExecuteAsync_SerializesSameUserAndAllowsDifferentUsers()
    {
        var coordinator = new UserLifecycleCoordinator();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sameUserEntered = false;
        var otherUserEntered = false;

        var first = coordinator.ExecuteAsync(firstUser, async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;

        var same = coordinator.ExecuteAsync(firstUser, _ =>
        {
            sameUserEntered = true;
            return Task.CompletedTask;
        });
        var other = coordinator.ExecuteAsync(secondUser, _ =>
        {
            otherUserEntered = true;
            return Task.CompletedTask;
        });

        await other;
        MSTestAssert.IsTrue(otherUserEntered);
        MSTestAssert.IsFalse(sameUserEntered);

        releaseFirst.SetResult();
        await Task.WhenAll(first, same);
        MSTestAssert.IsTrue(sameUserEntered);
    }
}
