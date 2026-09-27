using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

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

    public static void NormalizeDateTimeProperties(object? value)
    {
        if (value is null)
            return;

        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
                continue;

            if (property.PropertyType == typeof(DateTime))
            {
                var raw = property.GetValue(value);
                if (raw is DateTime dateTime)
                    property.SetValue(value, ToUtc(dateTime));
            }
            else if (property.PropertyType == typeof(DateTime?))
            {
                var raw = property.GetValue(value);
                property.SetValue(value, raw is DateTime dateTime ? ToUtc(dateTime) : null);
            }
            else if (property.PropertyType == typeof(DateTimeOffset))
            {
                var raw = property.GetValue(value);
                if (raw is DateTimeOffset dateTimeOffset)
                    property.SetValue(value, ToUtc(dateTimeOffset));
            }
            else if (property.PropertyType == typeof(DateTimeOffset?))
            {
                var raw = property.GetValue(value);
                property.SetValue(value, raw is DateTimeOffset dateTimeOffset ? ToUtc(dateTimeOffset) : null);
            }
        }
    }

    public static T NormalizeObjectGraph<T>(T value) where T : class
    {
        NormalizeObjectGraph(value, new HashSet<object>(UtcDateTimeReferenceEqualityComparer.Instance));
        return value;
    }

    private static void NormalizeObjectGraph(object? value, HashSet<object> visited)
    {
        if (value is null)
            return;

        var type = value.GetType();
        if (IsSimpleType(type))
            return;

        if (!visited.Add(value))
            return;

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
                NormalizeObjectGraph(item, visited);
            return;
        }

        NormalizeDateTimeProperties(value);

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            var propertyType = property.PropertyType;
            if (IsSimpleType(propertyType) || propertyType == typeof(DateTime?) || propertyType == typeof(DateTimeOffset?))
                continue;

            NormalizeObjectGraph(property.GetValue(value), visited);
        }
    }

    private static bool IsSimpleType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
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
