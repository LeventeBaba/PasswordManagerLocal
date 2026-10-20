using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public static class EndpointOperationTestAssertions
{
    public static void AreEquivalent<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string description,
        IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        comparer ??= EqualityComparer<T>.Default;
        var expectedItems = expected.ToArray();
        var actualItems = actual.ToArray();
        var expectedSet = expectedItems.ToHashSet(comparer);
        var actualSet = actualItems.ToHashSet(comparer);
        var expectedDuplicates = FindDuplicates(expectedItems, comparer);
        var actualDuplicates = FindDuplicates(actualItems, comparer);
        var missing = expectedSet.Except(actualSet, comparer).ToArray();
        var unexpected = actualSet.Except(expectedSet, comparer).ToArray();

        Assert.IsTrue(
            expectedDuplicates.Length == 0,
            $"{description} has duplicate expected entries: {string.Join(", ", expectedDuplicates)}.");
        Assert.IsTrue(
            actualDuplicates.Length == 0,
            $"{description} has duplicate actual entries: {string.Join(", ", actualDuplicates)}.");
        Assert.IsTrue(
            missing.Length == 0 && unexpected.Length == 0,
            $"{description} differs. Missing: [{string.Join(", ", missing)}]. " +
            $"Unexpected: [{string.Join(", ", unexpected)}].");
    }

    private static T[] FindDuplicates<T>(IEnumerable<T> items, IEqualityComparer<T> comparer)
        where T : notnull => items
        .GroupBy(item => item, comparer)
        .Where(group => group.Skip(1).Any())
        .Select(group => group.Key)
        .ToArray();
}
