using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>文件夹仓储，负责维护增量同步状态（last_max_uid / UIDVALIDITY）。</summary>
public sealed class SqliteFolderRepository : IFolderRepository
{
    private const string SelectColumns =
        "id, account_id, full_name, display_name, kind, uid_validity, last_max_uid, last_sync_utc, " +
        "unread_count, total_count, is_selectable";

    private readonly SqliteConnectionFactory _factory;

    public SqliteFolderRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task<IReadOnlyList<MailFolder>> GetByAccountAsync(long accountId, CancellationToken ct = default) =>
        QueryAsync("WHERE account_id = @accountId ORDER BY full_name", c => c.Parameters.AddWithValue("@accountId", accountId), ct);

    public Task<IReadOnlyList<MailFolder>> GetAllAsync(CancellationToken ct = default) =>
        QueryAsync("ORDER BY account_id, full_name", _ => { }, ct);

    public async Task<MailFolder?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM folders WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> UpsertAsync(MailFolder folder, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 冲突更新时保留既有同步游标：仅当调用方显式传入非 0 值时才覆盖，避免被同步过程的默认对象清零。
        command.CommandText = """
INSERT INTO folders(account_id, full_name, display_name, kind, uid_validity, last_max_uid, last_sync_utc,
                    unread_count, total_count, is_selectable)
VALUES(@accountId, @fullName, @displayName, @kind, @uidValidity, @lastMaxUid, @lastSync,
       @unread, @total, @selectable)
ON CONFLICT(account_id, full_name) DO UPDATE SET
    display_name  = excluded.display_name,
    kind          = excluded.kind,
    uid_validity  = CASE WHEN excluded.uid_validity <> 0 THEN excluded.uid_validity ELSE folders.uid_validity END,
    last_max_uid  = CASE WHEN excluded.last_max_uid <> 0 THEN excluded.last_max_uid ELSE folders.last_max_uid END,
    last_sync_utc = COALESCE(excluded.last_sync_utc, folders.last_sync_utc),
    unread_count  = excluded.unread_count,
    total_count   = excluded.total_count,
    is_selectable = excluded.is_selectable;
SELECT id FROM folders WHERE account_id = @accountId AND full_name = @fullName;
""";
        command.Parameters.AddWithValue("@accountId", folder.AccountId);
        command.Parameters.AddWithValue("@fullName", folder.FullName);
        command.Parameters.AddWithValue("@displayName", folder.DisplayName ?? string.Empty);
        command.Parameters.AddWithValue("@kind", (int)folder.Kind);
        command.Parameters.AddWithValue("@uidValidity", folder.UidValidity);
        command.Parameters.AddWithValue("@lastMaxUid", folder.LastMaxUid);
        command.Parameters.AddWithValue("@lastSync", (object?)SqlConvert.ToDb(folder.LastSyncUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@unread", folder.UnreadCount);
        command.Parameters.AddWithValue("@total", folder.TotalCount);
        command.Parameters.AddWithValue("@selectable", folder.IsSelectable ? 1 : 0);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        folder.Id = id;
        return id;
    }

    public async Task UpdateSyncStateAsync(long folderId, uint uidValidity, uint lastMaxUid, DateTimeOffset syncedUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE folders SET uid_validity = @uidValidity, last_max_uid = @lastMaxUid, last_sync_utc = @sync WHERE id = @id;
""";
        command.Parameters.AddWithValue("@uidValidity", uidValidity);
        command.Parameters.AddWithValue("@lastMaxUid", lastMaxUid);
        command.Parameters.AddWithValue("@sync", SqlConvert.ToDb(syncedUtc)!);
        command.Parameters.AddWithValue("@id", folderId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetFolderAsync(long folderId, uint newUidValidity, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            // UIDVALIDITY 变化：更新游标并清空本地邮件（含附件级联与 FTS 触发器同步），触发全量重同步。
            update.Transaction = tx;
            update.CommandText =
                "UPDATE folders SET uid_validity = @uidValidity, last_max_uid = 0, last_sync_utc = NULL WHERE id = @id;" +
                "DELETE FROM messages WHERE folder_id = @id;";
            update.Parameters.AddWithValue("@uidValidity", newUidValidity);
            update.Parameters.AddWithValue("@id", folderId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateCountsAsync(long folderId, int unreadCount, int totalCount, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE folders SET unread_count = @unread, total_count = @total WHERE id = @id;";
        command.Parameters.AddWithValue("@unread", unreadCount);
        command.Parameters.AddWithValue("@total", totalCount);
        command.Parameters.AddWithValue("@id", folderId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<MailFolder?> FindByKindAsync(long accountId, FolderKind kind, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM folders WHERE account_id = @accountId AND kind = @kind ORDER BY id LIMIT 1;";
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@kind", (int)kind);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task DeleteAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM folders WHERE id = @id;";
        command.Parameters.AddWithValue("@id", folderId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MailFolder>> QueryAsync(
        string tail, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM folders {tail};";
        bind(command);
        var result = new List<MailFolder>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }
        return result;
    }

    private static MailFolder Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        AccountId = SqlConvert.Long(reader, 1),
        FullName = SqlConvert.Str(reader, 2),
        DisplayName = SqlConvert.Str(reader, 3),
        Kind = (FolderKind)SqlConvert.Int(reader, 4),
        UidValidity = (uint)SqlConvert.Long(reader, 5),
        LastMaxUid = (uint)SqlConvert.Long(reader, 6),
        LastSyncUtc = SqlConvert.ParseDateNullable(reader, 7),
        UnreadCount = SqlConvert.Int(reader, 8),
        TotalCount = SqlConvert.Int(reader, 9),
        IsSelectable = SqlConvert.Bool(reader, 10)
    };
}
