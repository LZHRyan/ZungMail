using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>侧边栏上下文查询：同一发件人的往来邮件与附件。</summary>
public sealed class SqliteMailContextRepository : IMailContextRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMailContextRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<CorrespondenceItem>> GetCorrespondenceAsync(
        long accountId, string fromEmail, long excludeMessageId, int take = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromEmail)) return Array.Empty<CorrespondenceItem>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, subject, from_name, date_utc, flags, folder_id,
       COALESCE(preview, ''), COALESCE(has_attachment, 0)
FROM messages
WHERE account_id = @accountId
  AND from_email = @email COLLATE NOCASE
  AND id <> @currentId
ORDER BY date_utc DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@email", fromEmail.Trim());
        command.Parameters.AddWithValue("@currentId", excludeMessageId);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 200));

        var items = new List<CorrespondenceItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var flags = (MessageFlags)SqlConvert.Int(reader, 4);
            items.Add(new CorrespondenceItem(
                SqlConvert.Long(reader, 0),
                SqlConvert.Str(reader, 1),
                SqlConvert.Str(reader, 2),
                SqlConvert.ParseDate(reader.GetString(3)),
                (flags & MessageFlags.Seen) == 0,
                SqlConvert.Long(reader, 5),
                SqlConvert.Str(reader, 6),
                SqlConvert.Int(reader, 7) != 0));
        }
        return items;
    }

    public async Task<IReadOnlyList<SenderAttachmentItem>> GetSenderAttachmentsAsync(
        long accountId, string fromEmail, int take = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromEmail)) return Array.Empty<SenderAttachmentItem>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT a.message_id, a.id, a.file_name, a.size, a.local_path, m.subject, m.date_utc
FROM attachments a
JOIN messages m ON m.id = a.message_id
WHERE m.account_id = @accountId
  AND m.from_email = @email COLLATE NOCASE
  AND a.is_inline = 0
ORDER BY m.date_utc DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@email", fromEmail.Trim());
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 200));

        var items = new List<SenderAttachmentItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new SenderAttachmentItem(
                SqlConvert.Long(reader, 0),
                SqlConvert.Long(reader, 1),
                SqlConvert.Str(reader, 2),
                SqlConvert.Long(reader, 3),
                reader.IsDBNull(4) ? null : SqlConvert.Str(reader, 4),
                SqlConvert.Str(reader, 5),
                SqlConvert.ParseDate(reader.GetString(6))));
        }
        return items;
    }
}
