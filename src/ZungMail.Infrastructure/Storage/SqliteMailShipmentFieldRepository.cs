using Microsoft.Data.Sqlite;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 运输字段仓储：一封邮件一行（箱型箱量 / 航线 / 条款 / 金额）。</summary>
public sealed class SqliteMailShipmentFieldRepository : IMailShipmentFieldRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMailShipmentFieldRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task ReplaceRangeAsync(IReadOnlyList<ShipmentFieldEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow)!;

        foreach (var entry in entries)
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM message_shipment_fields WHERE email_id = @email;";
                delete.Parameters.AddWithValue("@email", entry.EmailId);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (!entry.Fields.HasAny) continue;

            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
INSERT INTO message_shipment_fields(
    email_id, boxes, lane_pol, lane_pod, lane_raw, incoterm, amount, currency, basis, created_utc)
VALUES(@email, @boxes, @pol, @pod, @laneRaw, @incoterm, @amount, @currency, @basis, @utc);
""";
            insert.Parameters.AddWithValue("@email", entry.EmailId);
            insert.Parameters.AddWithValue("@boxes", entry.Fields.BoxesText);
            insert.Parameters.AddWithValue("@pol", entry.Fields.Lane?.From ?? "");
            insert.Parameters.AddWithValue("@pod", entry.Fields.Lane?.To ?? "");
            insert.Parameters.AddWithValue("@laneRaw", entry.Fields.Lane?.Raw ?? "");
            insert.Parameters.AddWithValue("@incoterm", entry.Fields.Incoterm);
            insert.Parameters.AddWithValue("@amount", (object?)entry.Fields.Amount ?? DBNull.Value);
            insert.Parameters.AddWithValue("@currency", entry.Fields.Currency);
            insert.Parameters.AddWithValue("@basis", entry.Fields.Basis);
            insert.Parameters.AddWithValue("@utc", now);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ShipmentFields> GetForMessageAsync(long emailId, CancellationToken ct = default)
    {
        var map = await GetForMessagesAsync(new[] { emailId }, ct).ConfigureAwait(false);
        return map.TryGetValue(emailId, out var fields) ? fields : ShipmentFields.Empty;
    }

    public async Task<IReadOnlyDictionary<long, ShipmentFields>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, ShipmentFields>();
        if (emailIds.Count == 0) return result;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var names = new List<string>();
        for (var i = 0; i < emailIds.Count; i++)
        {
            var name = "@e" + i;
            names.Add(name);
            command.Parameters.AddWithValue(name, emailIds[i]);
        }

        command.CommandText = $"""
SELECT email_id, boxes, lane_pol, lane_pod, lane_raw, incoterm, amount, currency, basis
FROM message_shipment_fields
WHERE email_id IN ({string.Join(",", names)});
""";

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var boxes = ParseBoxes(reader.IsDBNull(1) ? "" : reader.GetString(1));
            var from = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var to = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var raw = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var lane = from.Length > 0 && to.Length > 0 ? new ShipmentLane(from, to, raw) : null;
            var incoterm = reader.IsDBNull(5) ? "" : reader.GetString(5);
            decimal? amount = reader.IsDBNull(6) ? null : (decimal)reader.GetDouble(6);
            var currency = reader.IsDBNull(7) ? "" : reader.GetString(7);
            var basis = reader.IsDBNull(8) ? "" : reader.GetString(8);

            result[reader.GetInt64(0)] = new ShipmentFields(boxes, lane, incoterm, amount, currency, basis);
        }

        return result;
    }

    /// <summary>把落库的 `1×40HC;3×20GP` 解析回结构化箱型；解析不出的条目直接跳过。</summary>
    private static IReadOnlyList<ShipmentBox> ParseBoxes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ShipmentBox>();

        var result = new List<ShipmentBox>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var times = trimmed.IndexOf('×');
            if (times <= 0) continue;

            if (!int.TryParse(trimmed[..times], out var count) || count <= 0) continue;

            var sizeAndType = trimmed[(times + 1)..];
            if (sizeAndType.Length < 3) continue;

            result.Add(new ShipmentBox(count, sizeAndType[..2], sizeAndType[2..]));
        }

        return result;
    }
}
