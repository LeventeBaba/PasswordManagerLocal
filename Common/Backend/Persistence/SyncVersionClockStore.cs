using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class SyncVersionClockStore(SqliteConnectionFactory connections) : ISyncVersionClockStore
{
    public string DatabaseKey
    {
        get
        {
            using var connection = connections.CreateConnection();
            return AppDatabaseTransactionRegistry.GetDatabaseKey(connection);
        }
    }

    public SyncVersionClockState? Read(int? timeoutSeconds = null)
    {
        using var connection = connections.CreateConnection();
        if (timeoutSeconds is int timeout)
            connection.DefaultTimeout = timeout;
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "LastPhysicalTimeUnixMilliseconds", "LastLogicalCounter", "LastUpdatedAtUtc", "Version"
            FROM "SyncVersionClockState" WHERE "Id" = $id;
            """;
        command.Parameters.AddWithValue("$id", SyncVersionClockState.SingletonId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SyncVersionClockState
            {
                LastPhysicalTimeUnixMilliseconds = reader.GetInt64(0),
                LastLogicalCounter = reader.GetInt64(1),
                LastUpdatedAtUtc = reader.GetDateTimeOffset(2),
                Version = reader.GetInt64(3)
            }
            : null;
    }

    public bool TryWrite(SyncVersionClockState state, long? expectedVersion, int? timeoutSeconds = null)
    {
        using var connection = connections.CreateConnection();
        if (timeoutSeconds is int timeout)
            connection.DefaultTimeout = timeout;
        connection.Open();

        // Each write is one atomic statement. A zero row count is either a
        // stale version or a concurrent insert of the singleton.
        using var command = connection.CreateCommand();
        if (expectedVersion is long version)
        {
            command.CommandText = """
                UPDATE "SyncVersionClockState"
                SET "LastPhysicalTimeUnixMilliseconds" = $physical,
                    "LastLogicalCounter" = $logical,
                    "LastUpdatedAtUtc" = $updatedAt,
                    "Version" = $version
                WHERE "Id" = $id AND "Version" = $expectedVersion;
                """;
            command.Parameters.AddWithValue("$expectedVersion", version);
        }
        else
        {
            command.CommandText = """
                INSERT INTO "SyncVersionClockState"
                    ("Id", "LastPhysicalTimeUnixMilliseconds", "LastLogicalCounter", "LastUpdatedAtUtc", "Version")
                VALUES ($id, $physical, $logical, $updatedAt, $version)
                ON CONFLICT ("Id") DO NOTHING;
                """;
        }

        command.Parameters.AddWithValue("$id", SyncVersionClockState.SingletonId);
        command.Parameters.AddWithValue("$physical", state.LastPhysicalTimeUnixMilliseconds);
        command.Parameters.AddWithValue("$logical", state.LastLogicalCounter);
        command.Parameters.AddWithValue("$updatedAt", UtcDateTimeUtil.ToUtc(state.LastUpdatedAtUtc));
        command.Parameters.AddWithValue("$version", state.Version);
        return command.ExecuteNonQuery() == 1;
    }
}
