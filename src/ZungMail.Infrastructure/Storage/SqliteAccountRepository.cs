using System.Security.Cryptography;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>账户仓储（FR-01）。凭据明文仅在内存中存在，落库前后分别经 ICredentialProtector 加解密。</summary>
public sealed class SqliteAccountRepository : IAccountRepository
{
    private const string SelectColumns =
        "id, name, email, display_name, username, secret_enc, auth_kind, imap_host, imap_port, " +
        "imap_secure, smtp_host, smtp_port, smtp_secure, is_enabled, sort_order, color, created_utc, fetch_policy, is_primary";

    private readonly SqliteConnectionFactory _factory;
    private readonly ICredentialProtector _protector;

    public SqliteAccountRepository(SqliteConnectionFactory factory, ICredentialProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 主账户永远排最前：侧栏顺序与同步顺序（SyncAllAsync 按本列表遍历）都以它为准
        command.CommandText = $"SELECT {SelectColumns} FROM accounts ORDER BY is_primary DESC, sort_order, id;";
        var result = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public async Task<Account?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM accounts WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> AddAsync(Account account, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // sort_order 缺省按创建顺序自增，保证列表稳定。
        command.CommandText = """
INSERT INTO accounts(name, email, display_name, username, secret_enc, auth_kind, imap_host, imap_port,
                     imap_secure, smtp_host, smtp_port, smtp_secure, is_enabled, sort_order, color, created_utc, fetch_policy, is_primary)
VALUES(@name, @email, @display, @username, @secret, @auth, @imapHost, @imapPort,
       @imapSecure, @smtpHost, @smtpPort, @smtpSecure, @enabled,
       COALESCE((SELECT MAX(sort_order) + 1 FROM accounts), 0), @color, @created, @fetchPolicy,
       -- 第一个账户自动成为主账户，之后新增的都是副账户
       CASE WHEN EXISTS(SELECT 1 FROM accounts WHERE is_primary = 1) THEN 0 ELSE 1 END);
SELECT last_insert_rowid();
""";
        Bind(command, account);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        account.Id = id;
        return id;
    }

    public async Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE accounts SET
    name = @name, email = @email, display_name = @display, username = @username,
    secret_enc = @secret, auth_kind = @auth, imap_host = @imapHost, imap_port = @imapPort,
    imap_secure = @imapSecure, smtp_host = @smtpHost, smtp_port = @smtpPort, smtp_secure = @smtpSecure,
    is_enabled = @enabled, sort_order = @sortOrder, color = @color, fetch_policy = @fetchPolicy
WHERE id = @id;
""";
        Bind(command, account);
        command.Parameters.AddWithValue("@sortOrder", account.SortOrder);
        command.Parameters.AddWithValue("@id", account.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>启用 / 停止使用账户：只切换状态，不删除本地邮件。</summary>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET is_enabled = @enabled WHERE id = @id;";
        command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    /// <summary>设置主账户：只保留一个主账户，其余自动降为副账户。</summary>
    public async Task SetPrimaryAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE accounts SET is_primary = CASE WHEN id = @id THEN 1 ELSE 0 END;";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, bool deleteMessages, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (deleteMessages)
        {
            // 依赖外键 ON DELETE CASCADE 级联清理 folders/messages/attachments（foreign_keys=ON）。
            command.CommandText = "DELETE FROM accounts WHERE id = @id;";
        }
        else
        {
            // 保留本地缓存邮件，仅停用账户。
            command.CommandText = "UPDATE accounts SET is_enabled = 0 WHERE id = @id;";
        }
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM accounts;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private void Bind(SqliteCommand command, Account account)
    {
        command.Parameters.AddWithValue("@name", account.Name ?? string.Empty);
        command.Parameters.AddWithValue("@email", account.Email ?? string.Empty);
        command.Parameters.AddWithValue("@display", account.DisplayName ?? string.Empty);
        command.Parameters.AddWithValue("@username", account.Username ?? string.Empty);
        command.Parameters.AddWithValue("@secret", Encrypt(account.Secret));
        command.Parameters.AddWithValue("@auth", (int)account.AuthKind);
        command.Parameters.AddWithValue("@imapHost", account.ImapHost ?? string.Empty);
        command.Parameters.AddWithValue("@imapPort", account.ImapPort);
        command.Parameters.AddWithValue("@imapSecure", (int)account.ImapSecure);
        command.Parameters.AddWithValue("@smtpHost", account.SmtpHost ?? string.Empty);
        command.Parameters.AddWithValue("@smtpPort", account.SmtpPort);
        command.Parameters.AddWithValue("@smtpSecure", (int)account.SmtpSecure);
        command.Parameters.AddWithValue("@enabled", account.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@color", account.Color ?? string.Empty);
        command.Parameters.AddWithValue("@created", SqlConvert.ToDb(account.CreatedUtc)!);
        command.Parameters.AddWithValue("@fetchPolicy", (int)account.FetchPolicy);
    }

    private string? Encrypt(string? plain) =>
        string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);

    private Account Map(SqliteDataReader reader)
    {
        var account = new Account
        {
            Id = SqlConvert.Long(reader, 0),
            Name = SqlConvert.Str(reader, 1),
            Email = SqlConvert.Str(reader, 2),
            DisplayName = SqlConvert.Str(reader, 3),
            Username = SqlConvert.Str(reader, 4),
            AuthKind = (AuthKind)SqlConvert.Int(reader, 6),
            ImapHost = SqlConvert.Str(reader, 7),
            ImapPort = SqlConvert.Int(reader, 8),
            ImapSecure = (SecureMode)SqlConvert.Int(reader, 9),
            SmtpHost = SqlConvert.Str(reader, 10),
            SmtpPort = SqlConvert.Int(reader, 11),
            SmtpSecure = (SecureMode)SqlConvert.Int(reader, 12),
            IsEnabled = SqlConvert.Bool(reader, 13),
            SortOrder = SqlConvert.Int(reader, 14),
            Color = SqlConvert.Str(reader, 15),
            CreatedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 16)),
            FetchPolicy = (FetchPolicy)SqlConvert.Int(reader, 17),
            IsPrimary = SqlConvert.Bool(reader, 18)
        };

        var secretEnc = SqlConvert.StrOrNull(reader, 5);
        if (!string.IsNullOrEmpty(secretEnc))
        {
            try
            {
                account.Secret = _protector.Unprotect(secretEnc);
            }
            catch (CryptographicException)
            {
                // 单条凭据损坏不应阻断账户列表加载，留空由界面提示重新输入。
                account.Secret = string.Empty;
            }
        }
        return account;
    }
}
