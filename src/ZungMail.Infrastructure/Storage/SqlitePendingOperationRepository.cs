using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>待同步操作队列仓储（本地优先写操作的持久化意图）。</summary>
public sealed class SqlitePendingOperationRepository : IPendingOperationRepository
{
    private const string Cols =
        "id, email_id, kind, state, flags, payload, attempts, last_error, created_utc, updated_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqlitePendingOperationRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<long> EnqueueAsync(PendingOperation operation, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 同一封邮件的同类操作只保留最新意图；批量操作（EmailId = 0）不去重。
        if (operation.EmailId > 0)
        {
            await using var dedupe = connection.CreateCommand();
            dedupe.Transaction = tx;
            dedupe.CommandText =
                "DELETE FROM pending_operations WHERE email_id = @emailId AND kind = @kind AND state <> @succeeded;";
            dedupe.Parameters.AddWithValue("@emailId", operation.EmailId);
            dedupe.Parameters.AddWithValue("@kind", (int)operation.Kind);
            dedupe.Parameters.AddWithValue("@succeeded", (int)PendingOperationState.Succeeded);
            await dedupe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
INSERT INTO pending_operations(email_id, kind, state, flags, payload, attempts, last_error, created_utc, updated_utc)
VALUES(@emailId, @kind, @state, @flags, @payload, @attempts, @error, @created, @updated);
SELECT last_insert_rowid();
""";
        var now = DateTimeOffset.UtcNow;
        insert.Parameters.AddWithValue("@emailId", operation.EmailId);
        insert.Parameters.AddWithValue("@kind", (int)operation.Kind);
        insert.Parameters.AddWithValue("@state", (int)operation.State);
        insert.Parameters.AddWithValue("@flags", (int)operation.Flags);
        insert.Parameters.AddWithValue("@payload", (object?)operation.Payload ?? DBNull.Value);
        insert.Parameters.AddWithValue("@attempts", operation.Attempts);
        insert.Parameters.AddWithValue("@error", (object?)operation.LastError ?? DBNull.Value);
        insert.Parameters.AddWithValue("@created", SqlConvert.ToDb(now)!);
        insert.Parameters.AddWithValue("@updated", SqlConvert.ToDb(now)!);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        operation.Id = id;
        operation.CreatedUtc = now;
        operation.UpdatedUtc = now;
        return id;
    }

    public async Task<IReadOnlyList<long>> EnqueueRangeAsync(
        IReadOnlyList<PendingOperation> operations, CancellationToken ct = default)
    {
        if (operations.Count == 0) return Array.Empty<long>();

        var ids = new List<long>(operations.Count);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1) 按 kind 去重：同一封邮件的同类未成功操作只留最新意图（整批一条 DELETE）
        foreach (var group in operations.Where(o => o.EmailId > 0).GroupBy(o => o.Kind))
        {
            var emailIds = group.Select(o => o.EmailId).Distinct().ToList();
            foreach (var chunk in emailIds.Chunk(400))
            {
                await using var dedupe = connection.CreateCommand();
                dedupe.Transaction = tx;
                var names = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    names[i] = "@e" + i;
                    dedupe.Parameters.AddWithValue(names[i], chunk[i]);
                }
                dedupe.CommandText =
                    $"DELETE FROM pending_operations WHERE kind = @kind AND state <> @succeeded " +
                    $"AND email_id IN ({string.Join(',', names)});";
                dedupe.Parameters.AddWithValue("@kind", (int)group.Key);
                dedupe.Parameters.AddWithValue("@succeeded", (int)PendingOperationState.Succeeded);
                await dedupe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        // 2) 同一条 INSERT 复用参数，避免 N 次语句编译
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
INSERT INTO pending_operations(email_id, kind, state, flags, payload, attempts, last_error, created_utc, updated_utc)
VALUES(@emailId, @kind, @state, @flags, @payload, @attempts, @error, @created, @updated);
SELECT last_insert_rowid();
""";
        var pEmailId = insert.Parameters.Add("@emailId", SqliteType.Integer);
        var pKind = insert.Parameters.Add("@kind", SqliteType.Integer);
        var pState = insert.Parameters.Add("@state", SqliteType.Integer);
        var pFlags = insert.Parameters.Add("@flags", SqliteType.Integer);
        var pPayload = insert.Parameters.Add("@payload", SqliteType.Text);
        var pAttempts = insert.Parameters.Add("@attempts", SqliteType.Integer);
        var pError = insert.Parameters.Add("@error", SqliteType.Text);
        var pCreated = insert.Parameters.Add("@created", SqliteType.Text);
        var pUpdated = insert.Parameters.Add("@updated", SqliteType.Text);

        foreach (var operation in operations)
        {
            ct.ThrowIfCancellationRequested();

            pEmailId.Value = operation.EmailId;
            pKind.Value = (int)operation.Kind;
            pState.Value = (int)operation.State;
            pFlags.Value = (int)operation.Flags;
            pPayload.Value = (object?)operation.Payload ?? DBNull.Value;
            pAttempts.Value = operation.Attempts;
            pError.Value = (object?)operation.LastError ?? DBNull.Value;
            pCreated.Value = SqlConvert.ToDb(now)!;
            pUpdated.Value = SqlConvert.ToDb(now)!;

            var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            operation.Id = id;
            operation.CreatedUtc = now;
            operation.UpdatedUtc = now;
            ids.Add(id);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ids;
    }

    public async Task<PendingOperation?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM pending_operations WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<PendingOperation>> GetOutstandingAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM pending_operations WHERE state <> @succeeded ORDER BY id;";
        command.Parameters.AddWithValue("@succeeded", (int)PendingOperationState.Succeeded);

        var result = new List<PendingOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public async Task<int> CountOutstandingAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending_operations WHERE state <> @succeeded;";
        command.Parameters.AddWithValue("@succeeded", (int)PendingOperationState.Succeeded);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task UpdateStateAsync(
        long id,
        PendingOperationState state,
        string? error = null,
        int attempts = 0,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE pending_operations
SET state = @state, last_error = @error, attempts = @attempts, updated_utc = @updated
WHERE id = @id;
""";
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@updated", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_operations WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> PruneSucceededAsync(DateTimeOffset olderThanUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM pending_operations WHERE state = @succeeded AND updated_utc < @before;";
        command.Parameters.AddWithValue("@succeeded", (int)PendingOperationState.Succeeded);
        command.Parameters.AddWithValue("@before", SqlConvert.ToDb(olderThanUtc)!);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static PendingOperation Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        EmailId = SqlConvert.Long(reader, 1),
        Kind = (PendingOperationKind)SqlConvert.Int(reader, 2),
        State = (PendingOperationState)SqlConvert.Int(reader, 3),
        Flags = (MessageFlags)SqlConvert.Int(reader, 4),
        Payload = SqlConvert.StrOrNull(reader, 5),
        Attempts = SqlConvert.Int(reader, 6),
        LastError = SqlConvert.StrOrNull(reader, 7),
        CreatedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 8)),
        UpdatedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 9))
    };
}
