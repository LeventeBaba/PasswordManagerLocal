using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class UtcDateTimeUtil
{
    public static readonly DateTime MinDateTime = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    public static DateTime ToUtc(DateTime value)
    {
        if (value == default)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static DateTime? ToUtc(DateTime? value) =>
        value.HasValue ? ToUtc(value.Value) : null;

    public static DateTimeOffset ToUtc(DateTimeOffset value) =>
        value == default ? default : value.ToUniversalTime();

    public static DateTimeOffset? ToUtc(DateTimeOffset? value) =>
        value.HasValue ? ToUtc(value.Value) : null;

    // The persisted and wire models already have source-generated JSON contracts.
    // Use their accessors so trimming does not affect which dates are normalized.
    internal static T NormalizeObjectGraph<T>(T value, JsonTypeInfo<T> typeInfo) where T : class
    {
        NormalizeObjectGraph(value, typeInfo.Options, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return value;
    }

    private static void NormalizeObjectGraph(object? value, JsonSerializerOptions options, HashSet<object> visited)
    {
        if (value is null)
            return;

        var type = value.GetType();
        if (IsSimpleType(type) || !visited.Add(value))
            return;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                NormalizeObjectGraph(item, options, visited);
            return;
        }

        foreach (var property in options.GetTypeInfo(type).Properties)
        {
            if (property.Get is not { } get)
                continue;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!IsSimpleType(propertyType))
            {
                NormalizeObjectGraph(get(value), options, visited);
                continue;
            }

            if (property.Set is not { } set)
                continue;

            if (propertyType == typeof(DateTime) && get(value) is DateTime dateTime)
                set(value, ToUtc(dateTime));
            else if (propertyType == typeof(DateTimeOffset) && get(value) is DateTimeOffset dateTimeOffset)
                set(value, ToUtc(dateTimeOffset));
        }
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(Guid) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) ||
               type == typeof(TimeSpan) ||
               type == typeof(byte[]);
    }

}
