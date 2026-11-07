using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class EfBackendChangeCapture
{
    public static BackendChangeSet Capture(ChangeTracker changeTracker)
    {
        var result = new BackendChangeSet();
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            if (string.Equals(entry.Metadata.GetTableName(), "GroupMembers", StringComparison.Ordinal))
                CaptureGroupMembership(result, entry);
            else
                result.AddEntityMutation(
                    entry.Entity,
                    entry.State is EntityState.Added or EntityState.Deleted,
                    property => Changed(entry, property));
        }

        return result;
    }

    private static void CaptureGroupMembership(BackendChangeSet changes, EntityEntry entry)
    {
        Guid? userId = null;
        foreach (var property in entry.Properties)
        {
            if (!property.Metadata.Name.Contains("User", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = property.CurrentValue ?? property.OriginalValue;
            if (value is Guid candidate && candidate != Guid.Empty)
            {
                userId = candidate;
                break;
            }
        }

        changes.Add(userId, FrontendChangeScope.Passwords);
    }

    private static bool Changed(EntityEntry entry, string propertyName)
    {
        var property = entry.Property(propertyName);
        return property.IsModified && !ValuesEqual(property.OriginalValue, property.CurrentValue);
    }

    private static bool ValuesEqual(object? original, object? current) =>
        original is byte[] originalBytes && current is byte[] currentBytes
            ? originalBytes.AsSpan().SequenceEqual(currentBytes)
            : Equals(original, current);
}
