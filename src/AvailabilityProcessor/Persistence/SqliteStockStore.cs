using System.Text.Json;
using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AvailabilityProcessor.Persistence;

/// <summary>
/// The applied versions and the event outbox, in one SQLite database. Both survive a restart, and an applied
/// version is stored in the same transaction as its output event.
/// One connection stays open for the life of the service and is used by one caller at a time. SQLite allows a
/// single writer anyway, and each statement here takes well under a millisecond.
/// In production this would be a server database shared by every instance of the service.
/// </summary>
public sealed class SqliteStockStore : IStockStateStore, IEventOutbox, IDisposable
{
    private const string Schema =
        """
        CREATE TABLE IF NOT EXISTS applied_stock (
            seller_id          TEXT    NOT NULL,
            product_id         TEXT    NOT NULL,
            size_id            TEXT    NOT NULL,
            country_code       TEXT    NOT NULL,
            source_version     INTEGER NOT NULL,
            available_quantity INTEGER NOT NULL,
            applied_at         INTEGER NOT NULL,
            PRIMARY KEY (seller_id, product_id, size_id, country_code)
        );

        CREATE TABLE IF NOT EXISTS outbox (
            event_id   TEXT    NOT NULL PRIMARY KEY,
            payload    TEXT    NOT NULL,
            created_at INTEGER NOT NULL
        );
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStockStore(IOptions<StorageOptions> options)
        : this(options.Value.ConnectionString)
    {
    }

    public SqliteStockStore(string connectionString)
    {
        // No pooling: closing this connection must really close the file.
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString());
        _connection.Open();

        // Write-ahead log, synced at checkpoints: a committed transaction survives a crash or restart of the service,
        // and a commit does not wait for the disk. Only a power failure can lose the most recent commits.
        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        Execute(Schema);
    }

    public async ValueTask<AppliedStock?> GetAsync(StockKey key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT source_version, available_quantity, applied_at
                FROM applied_stock
                WHERE seller_id = $seller AND product_id = $product AND size_id = $size AND country_code = $country
                """;
            AddKey(command, key);

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new AppliedStock(reader.GetInt64(0), reader.GetInt32(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)))
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecordAppliedAsync(StockKey key, AppliedStock state, SizeAvailabilityUpdated outputEvent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var upsert = _connection.CreateCommand())
            {
                upsert.Transaction = transaction;

                // The WHERE clause keeps versions monotonic by itself: a version that is not higher changes nothing.
                upsert.CommandText =
                    """
                    INSERT INTO applied_stock (seller_id, product_id, size_id, country_code, source_version, available_quantity, applied_at)
                    VALUES ($seller, $product, $size, $country, $version, $quantity, $appliedAt)
                    ON CONFLICT (seller_id, product_id, size_id, country_code) DO UPDATE SET
                        source_version = excluded.source_version,
                        available_quantity = excluded.available_quantity,
                        applied_at = excluded.applied_at
                    WHERE excluded.source_version > applied_stock.source_version
                    """;
                AddKey(upsert, key);
                upsert.Parameters.AddWithValue("$version", state.SourceVersion);
                upsert.Parameters.AddWithValue("$quantity", state.AvailableQuantity);
                upsert.Parameters.AddWithValue("$appliedAt", state.AppliedAt.ToUnixTimeMilliseconds());
                upsert.ExecuteNonQuery();
            }

            using (var enqueue = _connection.CreateCommand())
            {
                enqueue.Transaction = transaction;

                // The eventId is deterministic per stock key and version, so the same event is never queued twice.
                enqueue.CommandText = "INSERT OR IGNORE INTO outbox (event_id, payload, created_at) VALUES ($eventId, $payload, $createdAt)";
                enqueue.Parameters.AddWithValue("$eventId", outputEvent.EventId);
                enqueue.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(outputEvent, Json));
                enqueue.Parameters.AddWithValue("$createdAt", state.AppliedAt.ToUnixTimeMilliseconds());
                enqueue.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SizeAvailabilityUpdated>> GetPendingAsync(int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM outbox ORDER BY created_at, event_id LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);

            var pending = new List<SizeAvailabilityUpdated>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pending.Add(JsonSerializer.Deserialize<SizeAvailabilityUpdated>(reader.GetString(0), Json)!);
            }

            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> IsPendingAsync(string eventId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM outbox WHERE event_id = $eventId)";
            command.Parameters.AddWithValue("$eventId", eventId);
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask MarkPublishedAsync(string eventId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM outbox WHERE event_id = $eventId";
            command.Parameters.AddWithValue("$eventId", eventId);
            command.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> CountPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM outbox";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    private static void AddKey(SqliteCommand command, StockKey key)
    {
        command.Parameters.AddWithValue("$seller", key.SellerId);
        command.Parameters.AddWithValue("$product", key.ProductId);
        command.Parameters.AddWithValue("$size", key.SizeId);
        command.Parameters.AddWithValue("$country", key.CountryCode);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
