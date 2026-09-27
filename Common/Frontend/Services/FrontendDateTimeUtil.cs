using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>
/// Defines the frontend/backend time boundary.
/// Backend values are interpreted as UTC and converted to the device's current local time only for display.
/// Frontend values are converted to UTC before they are passed to backend request models.
/// </summary>
public static class FrontendDateTimeUtil
{
    public static DateTime ToLocalFromBackendUtc(DateTime value)
    {
        if (value == default)
            return default;

        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return utc.ToLocalTime();
    }

    public static DateTimeOffset ToLocalFromBackendUtc(DateTimeOffset value) =>
        value == default ? default : value.ToUniversalTime().ToLocalTime();

    public static DateTimeOffset? ToLocalFromBackendUtc(DateTimeOffset? value) =>
        value is { } dateTimeOffset ? ToLocalFromBackendUtc(dateTimeOffset) : null;

    public static DateTime ToTimeZoneFromBackendUtc(DateTime value, string timeZoneId)
    {
        if (value == default)
            return default;

        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var timeZone = ResolveTimeZone(timeZoneId);
        return timeZone is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
    }

    public static bool IsCurrentTimeZone(string timeZoneId) =>
        TimeZoneIdsMatch(timeZoneId, TimeZoneInfo.Local.Id);

    public static bool IsCurrentTimeZone(string timeZoneId, DateTime referenceUtc)
    {
        if (IsCurrentTimeZone(timeZoneId))
            return true;

        if (referenceUtc == default)
            return false;

        var registrationTimeZone = ResolveTimeZone(timeZoneId);
        if (registrationTimeZone is null)
            return false;

        var utc = referenceUtc.Kind == DateTimeKind.Utc
            ? referenceUtc
            : DateTime.SpecifyKind(referenceUtc, DateTimeKind.Utc);
        var registrationLocalTime = TimeZoneInfo.ConvertTimeFromUtc(utc, registrationTimeZone);
        var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        return registrationLocalTime == currentLocalTime;
    }

    public static string GetTimeZoneDisplayText(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return string.Empty;

        var timeZone = ResolveTimeZone(timeZoneId);
        if (timeZone is null || string.Equals(timeZone.DisplayName, timeZoneId, StringComparison.OrdinalIgnoreCase))
            return timeZoneId;

        return $"{timeZone.DisplayName} ({timeZoneId})";
    }

    public static DateTime ToBackendUtc(DateTime value)
    {
        if (value == default)
            return default;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };
    }

    public static DateTime? ToBackendUtc(DateTime? value) =>
        value is { } dateTime ? ToBackendUtc(dateTime) : null;

    public static DateTimeOffset ToBackendUtc(DateTimeOffset value) =>
        value == default ? default : value.ToUniversalTime();

    public static DateTimeOffset? ToBackendUtc(DateTimeOffset? value) =>
        value is { } dateTimeOffset ? ToBackendUtc(dateTimeOffset) : null;

    public static T NormalizeRequestToUtc<T>(T request)
    {
        if (request is null)
            return request;

        NormalizeObjectGraph(request, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return request;
    }

    private static void NormalizeObjectGraph(object value, HashSet<object> visited)
    {
        var type = value.GetType();
        if (IsSimpleType(type) || !visited.Add(value))
            return;

        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                    NormalizeObjectGraph(item, visited);
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                continue;

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (property.PropertyType == typeof(DateTime) && property.CanWrite && propertyValue is DateTime dateTime)
            {
                property.SetValue(value, ToBackendUtc(dateTime));
                continue;
            }

            if (property.PropertyType == typeof(DateTime?) && property.CanWrite)
            {
                property.SetValue(value, propertyValue is DateTime nullableDateTime ? ToBackendUtc(nullableDateTime) : null);
                continue;
            }

            if (property.PropertyType == typeof(DateTimeOffset) && property.CanWrite && propertyValue is DateTimeOffset dateTimeOffset)
            {
                property.SetValue(value, ToBackendUtc(dateTimeOffset));
                continue;
            }

            if (property.PropertyType == typeof(DateTimeOffset?) && property.CanWrite)
            {
                property.SetValue(value, propertyValue is DateTimeOffset nullableDateTimeOffset ? ToBackendUtc(nullableDateTimeOffset) : null);
                continue;
            }

            if (propertyValue is not null && !IsSimpleType(property.PropertyType))
                NormalizeObjectGraph(propertyValue, visited);
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return null;

        if (TryFindTimeZone(timeZoneId, out var direct))
            return direct;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId)
            && TryFindTimeZone(ianaId, out var ianaTimeZone))
        {
            return ianaTimeZone;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId)
            && TryFindTimeZone(windowsId, out var windowsTimeZone))
        {
            return windowsTimeZone;
        }

        return null;
    }

    private static bool TryFindTimeZone(string? timeZoneId, out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool TimeZoneIdsMatch(string firstTimeZoneId, string secondTimeZoneId)
    {
        if (string.Equals(firstTimeZoneId, secondTimeZoneId, StringComparison.OrdinalIgnoreCase))
            return true;

        var firstEquivalentIds = GetEquivalentTimeZoneIds(firstTimeZoneId);
        var secondEquivalentIds = GetEquivalentTimeZoneIds(secondTimeZoneId);
        if (firstEquivalentIds.Overlaps(secondEquivalentIds))
            return true;

        var firstTimeZone = ResolveTimeZone(firstTimeZoneId);
        var secondTimeZone = ResolveTimeZone(secondTimeZoneId);
        return firstTimeZone is not null
            && secondTimeZone is not null
            && firstTimeZone.HasSameRules(secondTimeZone);
    }

    private static HashSet<string> GetEquivalentTimeZoneIds(string timeZoneId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return ids;

        ids.Add(timeZoneId);
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) && !string.IsNullOrWhiteSpace(ianaId))
            ids.Add(ianaId);
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) && !string.IsNullOrWhiteSpace(windowsId))
            ids.Add(windowsId);

        var resolved = ResolveTimeZone(timeZoneId);
        if (resolved is not null)
            ids.Add(resolved.Id);

        return ids;
    }

    private static bool IsSimpleType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return underlyingType.IsPrimitive ||
               underlyingType.IsEnum ||
               underlyingType == typeof(string) ||
               underlyingType == typeof(decimal) ||
               underlyingType == typeof(Guid) ||
               underlyingType == typeof(DateTime) ||
               underlyingType == typeof(DateTimeOffset) ||
               underlyingType == typeof(TimeSpan);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
