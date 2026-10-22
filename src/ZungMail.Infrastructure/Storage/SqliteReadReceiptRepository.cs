using ZungMail.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>阅读收条发送记录：同一封邮件只提示一次，避免重复打扰与重复回执。</summary>
public sealed class SqliteReadReceiptRepository : IReadReceiptRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteReadReceiptRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<bool> IsSentAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM read_receipts WHERE email_id = @id;";
        command.Parameters.AddWithValue("@id", emailId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task MarkSentAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO read_receipts(email_id, sent_utc) VALUES(@id, @utc)
ON CONFLICT(email_id) DO UPDATE SET sent_utc = @utc;
""";
        command.Parameters.AddWithValue("@id", emailId);
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
