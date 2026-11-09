using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync;

[TestClass]
public sealed class UserSnapshotReceiptProtocolTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void EveryReceiptStateMatchesItsProtobufWireValue()
    {
        var receiptStates = Enum.GetValues<UserSnapshotReceiptState>();
        MSTestAssert.HasCount(receiptStates.Length, Enum.GetValues<UserSnapshotReceiptStateProto>());

        foreach (var state in receiptStates)
        {
            var wireState = Enum.Parse<UserSnapshotReceiptStateProto>($"UserSnapshotReceipt{state}");
            MSTestAssert.AreEqual((int)state, (int)wireState, state.ToString());
        }
    }
}
