using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>联系人仓储（FR-10），以规范化（小写）邮箱为唯一键去重。</summary>
public sealed class SqliteContactRepository : IContactRepository
{
    private const string SelectColumns = "id, email, name, last_used_utc, use_count, is_whitelisted";

    private readonly SqliteConnectionFactory _factory;

    public SqliteContactRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Contact>> SuggestAsync(string prefix, int take = 8, CancellationToken ct = default)
    {
        var p = (prefix ?? string.Empty).Trim().ToLowerInvariant();
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 排序：名称前缀命中优先，其次邮箱前缀命中，最后按使用频次。
        command.CommandText = $"""
SELECT {SelectColumns} FROM contacts
WHERE email LIKE @p OR name LIKE @p
ORDER BY (CASE WHEN name LIKE @p THEN 0 ELSE 1 END), use_count DESC, last_used_utc DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@p", p + "%");
        command.Parameters.AddWithValue("@take", take <= 0 ? 8 : take);
        return await ReadListAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Contact>> GetAllAsync(string? filter = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(filter))
        {
            command.CommandText = $"SELECT {SelectColumns} FROM contacts ORDER BY use_count DESC, email;";
        }
        else
        {
            command.CommandText = $"""
SELECT {SelectColumns} FROM contacts
WHERE email LIKE @f OR name LIKE @f
ORDER BY use_count DESC, email;
""";
            command.Parameters.AddWithValue("@f", "%" + filter.Trim() + "%");
        }
        return await ReadListAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<PagedResult<Contact>> QueryAsync(
        string? filter, int skip, int take, CancellationToken ct = default)
    {
        if (take <= 0) return PagedResult<Contact>.Empty;

        var hasFilter = !string.IsNullOrWhiteSpace(filter);
        var where = hasFilter ? "WHERE email LIKE @f OR name LIKE @f" : "";

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        int total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM contacts {where};";
            if (hasFilter) countCmd.Parameters.AddWithValue("@f", "%" + filter!.Trim() + "%");
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }

        var items = new List<Contact>(Math.Min(take, total));
        if (total > 0)
        {
            await using var pageCmd = connection.CreateCommand();
            pageCmd.CommandText = $"""
SELECT {SelectColumns} FROM contacts
{where}
ORDER BY use_count DESC, email
LIMIT @take OFFSET @skip;
""";
            if (hasFilter) pageCmd.Parameters.AddWithValue("@f", "%" + filter!.Trim() + "%");
            pageCmd.Parameters.AddWithValue("@take", take);
            pageCmd.Parameters.AddWithValue("@skip", Math.Max(0, skip));
            items.AddRange(await ReadListAsync(pageCmd, ct).ConfigureAwait(false));
        }

        return new PagedResult<Contact>
        {
            Items = items,
            TotalCount = total,
            HasMore = skip + items.Count < total
        };
    }

    public async Task CollectAsync(IEnumerable<EmailAddress> addresses, CancellationToken ct = default)
    {
        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        // 重复往来只累加计数、刷新名称与时间，不产生重复行（FR-10 验收）。
        command.CommandText = """
INSERT INTO contacts(email, name, last_used_utc, use_count, is_whitelisted)
VALUES(@email, @name, @now, 1, 0)
ON CONFLICT(email) DO UPDATE SET
    use_count     = contacts.use_count + 1,
    last_used_utc = excluded.last_used_utc,
    name          = CASE WHEN excluded.name <> '' THEN excluded.name ELSE contacts.name END;
""";
        var emailParam = command.Parameters.Add("@email", SqliteType.Text);
        var nameParam = command.Parameters.Add("@name", SqliteType.Text);
        command.Parameters.AddWithValue("@now", now);
        foreach (var address in addresses)
        {
            var email = (address.Address ?? string.Empty).Trim().ToLowerInvariant();
            if (email.Length == 0) continue;
            emailParam.Value = email;
            nameParam.Value = (address.Name ?? string.Empty).Trim();
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> UpsertManyAsync(IEnumerable<Contact> contacts, CancellationToken ct = default)
    {
        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow);
        var written = 0;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        // 导入不是往来：新联系人使用次数从 0 起，已存在时只刷新名称，不累加计数。
        command.CommandText = """
INSERT INTO contacts(email, name, last_used_utc, use_count, is_whitelisted)
VALUES(@email, @name, @now, 0, 0)
ON CONFLICT(email) DO UPDATE SET
    name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE contacts.name END;
""";
        var emailParam = command.Parameters.Add("@email", SqliteType.Text);
        var nameParam = command.Parameters.Add("@name", SqliteType.Text);
        command.Parameters.AddWithValue("@now", now);

        foreach (var contact in contacts)
        {
            var email = (contact.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal)) continue;

            emailParam.Value = email;
            nameParam.Value = (contact.Name ?? string.Empty).Trim();
            written += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written;
    }

    public async Task SetWhitelistAsync(string email, bool whitelisted, CancellationToken ct = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE contacts SET is_whitelisted = @w WHERE email = @email;";
        command.Parameters.AddWithValue("@w", whitelisted ? 1 : 0);
        command.Parameters.AddWithValue("@email", normalized);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM contacts WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Contact>> ReadListAsync(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<Contact>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Contact
            {
                Id = SqlConvert.Long(reader, 0),
                Email = SqlConvert.Str(reader, 1),
                Name = SqlConvert.Str(reader, 2),
                LastUsedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 3)),
                UseCount = SqlConvert.Int(reader, 4),
                IsWhitelisted = SqlConvert.Bool(reader, 5)
            });
        }
        return result;
    }
}
