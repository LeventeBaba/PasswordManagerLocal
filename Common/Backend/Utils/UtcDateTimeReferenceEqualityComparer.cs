using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal sealed class UtcDateTimeReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly UtcDateTimeReferenceEqualityComparer Instance = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
