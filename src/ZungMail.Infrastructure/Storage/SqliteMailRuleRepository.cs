using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 规则仓储。</summary>
public sealed class SqliteMailRuleRepository : IMailRuleRepository
{
    private const string Columns =
        "id, name, from_contains, subject_contains, from_domain, action, action_arg, target_folder_id, enabled, " +
        "hit_count, last_hit_utc, continue_after_match";

    private readonly SqliteConnectionFactory _factory;

    public SqliteMailRuleRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<MailRule>> GetAllAsync(bool onlyEnabled = false, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM mail_rules" +
                              (onlyEnabled ? " WHERE enabled = 1" : "") +
                              " ORDER BY id;";

        var rules = new List<MailRule>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rules.Add(Map(reader));
        return rules;
    }

    public async Task<MailRule?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM mail_rules WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> UpsertAsync(MailRule rule, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (rule.Id == 0)
        {
            command.CommandText = """
INSERT INTO mail_rules(name, from_contains, subject_contains, from_domain, action, action_arg, target_folder_id, enabled, hit_count, last_hit_utc, continue_after_match)
VALUES(@name, @from, @subject, @domain, @action, @arg, @folder, @enabled, @hits, @lastHit, @continueAfter);
SELECT last_insert_rowid();
""";
        }
        else
        {
            command.CommandText = """
UPDATE mail_rules SET name = @name, from_contains = @from, subject_contains = @subject,
       from_domain = @domain, action = @action, action_arg = @arg,
       target_folder_id = @folder, enabled = @enabled,
       continue_after_match = @continueAfter
WHERE id = @id;
SELECT @id;
""";
            command.Parameters.AddWithValue("@id", rule.Id);
        }

        command.Parameters.AddWithValue("@name", rule.Name ?? string.Empty);
        command.Parameters.AddWithValue("@from", rule.FromContains ?? string.Empty);
        command.Parameters.AddWithValue("@subject", rule.SubjectContains ?? string.Empty);
        command.Parameters.AddWithValue("@domain", rule.FromDomain ?? string.Empty);
        command.Parameters.AddWithValue("@arg", rule.ActionArg ?? string.Empty);
        command.Parameters.AddWithValue("@action", (int)rule.Action);
        command.Parameters.AddWithValue("@folder", (object?)rule.TargetFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("@enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@continueAfter", rule.ContinueAfterMatch ? 1 : 0);
        command.Parameters.AddWithValue("@hits", rule.HitCount);
        command.Parameters.AddWithValue("@lastHit", (object?)SqlConvert.ToDb(rule.LastHitUtc) ?? DBNull.Value);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        rule.Id = id;
        return id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mail_rules WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordHitAsync(long id, DateTimeOffset hitUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_rules SET hit_count = hit_count + 1, last_hit_utc = @utc WHERE id = @id;";
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(hitUtc)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static MailRule Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        Name = SqlConvert.Str(reader, 1),
        FromContains = SqlConvert.Str(reader, 2),
        SubjectContains = SqlConvert.Str(reader, 3),
        FromDomain = SqlConvert.Str(reader, 4),
        Action = (MailRuleAction)SqlConvert.Int(reader, 5),
        ActionArg = SqlConvert.Str(reader, 6),
        TargetFolderId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        Enabled = SqlConvert.Bool(reader, 8),
        HitCount = SqlConvert.Int(reader, 9),
        LastHitUtc = SqlConvert.ParseDateNullable(reader, 10),
        ContinueAfterMatch = SqlConvert.Bool(reader, 11)
    };
}
