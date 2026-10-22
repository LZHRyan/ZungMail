using System.Globalization;
using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 历史邮件清理引擎。删除采用「隔离区」策略：先记录完整快照并把附件目录移动到
/// <c>quarantine/&lt;jobId&gt;/&lt;messageId&gt;</c>，保留期内可完整撤销；
/// 彻底删除后才释放空间。所有任务与条目写入审计表，可追溯。
/// </summary>
public sealed class CleanupService : ICleanupService
{
    /// <summary>单批处理数量：兼顾吞吐与取消响应速度。</summary>
    private const int BatchSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly IMessageRepository _messages;
    private readonly IAppPaths _paths;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        SqliteConnectionFactory factory,
        IMessageRepository messages,
        IAppPaths paths,
        ILogger<CleanupService> logger)
    {
        _factory = factory;
        _messages = messages;
        _paths = paths;
        _logger = logger;
    }

    private string QuarantineRoot => Path.Combine(_paths.RootDirectory, "quarantine");

    // ---------------- 预览 ----------------

    public async Task<CleanupPreview> PreviewAsync(CleanupFilter filter, int take = 200, CancellationToken ct = default)
    {
        var (joins, where, prms) = BuildWhere(filter, excludeJobId: 0);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        int total;
        long totalBytes;
        long attachmentBytes;
        await using (var summary = connection.CreateCommand())
        {
            summary.CommandText = $"""
SELECT COUNT(*),
       COALESCE(SUM(m.size), 0),
       COALESCE(SUM(COALESCE((SELECT SUM(a.size) FROM attachments a
                              WHERE a.message_id = m.id AND a.is_inline = 0), 0)), 0)
FROM messages m {joins}
WHERE {where};
""";
            Apply(summary, prms);
            await using var reader = await summary.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            total = reader.GetInt32(0);
            totalBytes = reader.GetInt64(1);
            attachmentBytes = reader.GetInt64(2);
        }

        var items = new List<CleanupPreviewItem>();
        if (total > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
SELECT m.id, m.account_id, m.folder_id, COALESCE(f.display_name, f.full_name),
       m.subject, m.from_email, m.date_utc, m.size, m.flags,
       COALESCE((SELECT SUM(a.size) FROM attachments a
                 WHERE a.message_id = m.id AND a.is_inline = 0), 0)
FROM messages m {joins}
WHERE {where}
ORDER BY m.date_utc DESC, m.id DESC
LIMIT @take;
""";
            Apply(command, prms);
            command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 2000));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new CleanupPreviewItem
                {
                    Id = reader.GetInt64(0),
                    AccountId = reader.GetInt64(1),
                    FolderId = reader.GetInt64(2),
                    FolderName = SqlConvert.Str(reader, 3),
                    Subject = SqlConvert.Str(reader, 4),
                    FromEmail = SqlConvert.Str(reader, 5),
                    DateUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 6)),
                    Size = reader.GetInt64(7),
                    Flags = (MessageFlags)reader.GetInt32(8),
                    AttachmentBytes = reader.GetInt64(9)
                });
            }
        }

        return new CleanupPreview
        {
            Items = items,
            TotalCount = total,
            TotalBytes = totalBytes,
            AttachmentBytes = attachmentBytes
        };
    }

    // ---------------- 执行 ----------------

    public async Task<CleanupJob> ExecuteAsync(
        CleanupFilter filter,
        IReadOnlyCollection<long>? selectedIds,
        CleanupMode mode,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var job = new CleanupJob
        {
            Mode = mode,
            Status = CleanupStatus.Running,
            StartedUtc = DateTimeOffset.UtcNow,
            FilterJson = JsonSerializer.Serialize(new JobPayload
            {
                Filter = filter,
                SelectedIds = selectedIds?.ToList()
            }, JsonOptions)
        };

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        job.TotalCount = selectedIds is { Count: > 0 }
            ? selectedIds.Count
            : await CountAsync(connection, filter, ct).ConfigureAwait(false);

        job.Id = await InsertJobAsync(connection, job, ct).ConfigureAwait(false);

        return await RunAsync(job, filter, selectedIds?.ToList(), excludeJobId: 0, progress, ct)
            .ConfigureAwait(false);
    }

    public async Task<CleanupJob?> ResumeAsync(
        long jobId, IProgress<CleanupProgress>? progress = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || !job.CanResume) return null;

        JobPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JobPayload>(job.FilterJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload?.Filter is null) return null;

        await using (var connection = await _factory.CreateAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE cleanup_jobs SET status = 0, finished_utc = NULL, note = NULL WHERE id = @id;";
            command.Parameters.AddWithValue("@id", jobId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        job.Status = CleanupStatus.Running;
        job.FinishedUtc = null;
        job.Note = "";

        return await RunAsync(job, payload.Filter, payload.SelectedIds, excludeJobId: job.Id, progress, ct)
            .ConfigureAwait(false);
    }

    private async Task<CleanupJob> RunAsync(
        CleanupJob job,
        CleanupFilter filter,
        IReadOnlyList<long>? selectedIds,
        long excludeJobId,
        IProgress<CleanupProgress>? progress,
        CancellationToken ct)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var processed = job.ProcessedCount;
        long bytes = job.TotalBytes;

        try
        {
            if (selectedIds is { Count: > 0 })
            {
                foreach (var chunk in selectedIds.Chunk(BatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var (count, size) = await ProcessBatchAsync(connection, job, chunk, ct).ConfigureAwait(false);
                    processed += count;
                    bytes += size;
                    await UpdateProgressAsync(connection, job.Id, processed, bytes, ct).ConfigureAwait(false);
                    progress?.Report(new CleanupProgress
                    {
                        Processed = processed,
                        Total = Math.Max(job.TotalCount, processed),
                        Bytes = bytes
                    });
                }
            }
            else
            {
                long lastId = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = await SelectIdBatchAsync(connection, filter, lastId, excludeJobId, ct).ConfigureAwait(false);
                    if (ids.Count == 0) break;

                    var (count, size) = await ProcessBatchAsync(connection, job, ids, ct).ConfigureAwait(false);
                    processed += count;
                    bytes += size;
                    lastId = ids[^1];

                    await UpdateProgressAsync(connection, job.Id, processed, bytes, ct).ConfigureAwait(false);
                    progress?.Report(new CleanupProgress
                    {
                        Processed = processed,
                        Total = Math.Max(job.TotalCount, processed),
                        Bytes = bytes
                    });
                }
            }

            job.Status = CleanupStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = CleanupStatus.Cancelled;
            job.Note = "用户中断，已处理部分可撤销或恢复";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理任务 {JobId} 执行失败", job.Id);
            job.Status = CleanupStatus.Cancelled;
            job.Note = ex.Message;
        }

        job.ProcessedCount = processed;
        job.TotalBytes = bytes;
        job.FinishedUtc = DateTimeOffset.UtcNow;

        // 终止阶段不再响应取消，确保审计状态落库
        await FinishJobAsync(connection, job, CancellationToken.None).ConfigureAwait(false);
        return job;
    }

    /// <summary>按 id 游标取一批待清理邮件，避免一次性把百万 id 读入内存。</summary>
    private static async Task<List<long>> SelectIdBatchAsync(
        SqliteConnection connection, CleanupFilter filter, long lastId, long excludeJobId, CancellationToken ct)
    {
        var (joins, where, prms) = BuildWhere(filter, excludeJobId);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT m.id FROM messages m {joins}
WHERE {where} AND m.id > @lastId
ORDER BY m.id
LIMIT {BatchSize};
""";
        Apply(command, prms);
        command.Parameters.AddWithValue("@lastId", lastId);
        var ids = new List<long>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private async Task<(int Count, long Bytes)> ProcessBatchAsync(
        SqliteConnection connection, CleanupJob job, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var details = await LoadDetailsAsync(connection, ids, ct).ConfigureAwait(false);
        if (details.Count == 0) return (0, 0);

        long bytes = 0;
        await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            foreach (var detail in details)
            {
                var attachmentBytes = detail.Attachments
                    .Where(a => !a.IsInline)
                    .Sum(a => a.Size);
                bytes += detail.Size + attachmentBytes;

                var emlRel = $"{detail.AccountId.ToString(CultureInfo.InvariantCulture)}/" +
                             detail.Id.ToString(CultureInfo.InvariantCulture);

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = """
INSERT INTO cleanup_items(job_id, message_id, account_id, folder_id, folder_name, subject,
                          from_email, date_utc, size, attachment_bytes, payload_json, eml_rel_path)
VALUES(@jobId, @messageId, @accountId, @folderId, @folderName, @subject,
       @fromEmail, @date, @size, @attachmentBytes, @payload, @emlRel);
""";
                    insert.Parameters.AddWithValue("@jobId", job.Id);
                    insert.Parameters.AddWithValue("@messageId", detail.Id);
                    insert.Parameters.AddWithValue("@accountId", detail.AccountId);
                    insert.Parameters.AddWithValue("@folderId", detail.FolderId);
                    insert.Parameters.AddWithValue("@folderName", detail.FolderName);
                    insert.Parameters.AddWithValue("@subject", detail.Subject);
                    insert.Parameters.AddWithValue("@fromEmail", detail.FromEmail ?? string.Empty);
                    insert.Parameters.AddWithValue("@date", detail.DateUtc.ToUniversalTime()
                        .ToString("o", CultureInfo.InvariantCulture));
                    insert.Parameters.AddWithValue("@size", detail.Size);
                    insert.Parameters.AddWithValue("@attachmentBytes", attachmentBytes);
                    insert.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(detail, JsonOptions));
                    insert.Parameters.AddWithValue("@emlRel", emlRel);
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = $"DELETE FROM messages WHERE id IN ({Placeholders(ids.Count)});";
                AddIdParameters(delete, ids);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // 数据库提交后再移动附件目录；即使移动失败，purge 仍可按原路径清理
        foreach (var detail in details)
        {
            MoveToQuarantine(job.Id, detail.AccountId, detail.Id);
        }

        return (details.Count, bytes);
    }

    /// <summary>一次取出一批邮件的完整快照（含附件），用于写入审计与撤销恢复。</summary>
    private static async Task<List<EmailDetail>> LoadDetailsAsync(
        SqliteConnection connection, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var details = new Dictionary<long, EmailDetail>(ids.Count);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
SELECT m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.thread_id, m.subject,
       m.from_name, m.from_email, m.to_json, m.cc_json, m.bcc_json, m.reply_to,
       m.references_json, m.date_utc, m.preview, m.flags, m.size, m.has_attachment,
       m.body_state, m.body_html, m.body_text, COALESCE(f.display_name, f.full_name)
FROM messages m
JOIN folders f ON f.id = m.folder_id
WHERE m.id IN ({Placeholders(ids.Count)});
""";
            AddIdParameters(command, ids);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var detail = new EmailDetail
                {
                    Id = SqlConvert.Long(reader, 0),
                    AccountId = SqlConvert.Long(reader, 1),
                    FolderId = SqlConvert.Long(reader, 2),
                    Uid = (uint)SqlConvert.Long(reader, 3),
                    MessageId = SqlConvert.Str(reader, 4),
                    ThreadId = SqlConvert.Str(reader, 5),
                    Subject = SqlConvert.Str(reader, 6),
                    FromName = SqlConvert.Str(reader, 7),
                    FromEmail = SqlConvert.Str(reader, 8),
                    To = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 9)),
                    Cc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 10)),
                    Bcc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 11)),
                    References = JsonHelpers.DeserializeStrings(SqlConvert.StrOrNull(reader, 13)),
                    DateUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 14)),
                    Preview = SqlConvert.Str(reader, 15),
                    Flags = (MessageFlags)SqlConvert.Int(reader, 16),
                    Size = SqlConvert.Long(reader, 17),
                    HasAttachment = SqlConvert.Bool(reader, 18),
                    BodyState = (BodyState)SqlConvert.Int(reader, 19),
                    BodyHtml = SqlConvert.Str(reader, 20),
                    BodyText = SqlConvert.Str(reader, 21),
                    FolderName = SqlConvert.Str(reader, 22)
                };

                var replyTo = SqlConvert.Str(reader, 12);
                if (!string.IsNullOrEmpty(replyTo))
                {
                    detail.ReplyTo = new EmailAddress(replyTo);
                }
                details[detail.Id] = detail;
            }
        }

        if (details.Count == 0) return new List<EmailDetail>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
SELECT message_id, file_name, content_type, size, is_inline, local_path
FROM attachments
WHERE message_id IN ({Placeholders(ids.Count)});
""";
            AddIdParameters(command, ids);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!details.TryGetValue(reader.GetInt64(0), out var detail)) continue;
                detail.Attachments.Add(new AttachmentInfo
                {
                    FileName = SqlConvert.Str(reader, 1),
                    ContentType = SqlConvert.Str(reader, 2),
                    Size = SqlConvert.Long(reader, 3),
                    IsInline = SqlConvert.Bool(reader, 4),
                    LocalPath = SqlConvert.StrOrNull(reader, 5)
                });
            }
        }

        return details.Values.ToList();
    }

    // ---------------- 撤销 / 彻底删除 ----------------

    public async Task<int> UndoAsync(long jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || !job.CanUndo) return 0;

        var restored = 0;
        long lastItemId = 0;
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<(long ItemId, long MessageId, string Payload, string EmlRel)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
SELECT id, message_id, payload_json, eml_rel_path
FROM cleanup_items
WHERE job_id = @jobId AND id > @lastId
ORDER BY id
LIMIT 100;
""";
                select.Parameters.AddWithValue("@jobId", jobId);
                select.Parameters.AddWithValue("@lastId", lastItemId);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add((
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
                }
            }

            if (items.Count == 0) break;

            foreach (var item in items)
            {
                lastItemId = item.ItemId;
                EmailDetail? detail;
                try
                {
                    detail = JsonSerializer.Deserialize<EmailDetail>(item.Payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "清理条目 {ItemId} 快照损坏，跳过恢复", item.ItemId);
                    continue;
                }
                if (detail is null) continue;

                try
                {
                    if (await _messages.RestoreAsync(detail, ct).ConfigureAwait(false))
                    {
                        MoveFromQuarantine(jobId, item.MessageId, item.EmlRel);
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    // 文件夹已被删除、UID 冲突等情况：跳过该封，不影响其它邮件恢复
                    _logger.LogWarning(ex, "恢复邮件 {MessageId} 失败", item.MessageId);
                }
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
UPDATE cleanup_jobs
SET status = @status, finished_utc = @finished, processed_count = @processed
WHERE id = @id;
""";
            update.Parameters.AddWithValue("@status", (int)CleanupStatus.Undone);
            update.Parameters.AddWithValue("@finished", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("@processed", 0);
            update.Parameters.AddWithValue("@id", jobId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return restored;
    }

    public async Task<long> PurgeAsync(long jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status == CleanupStatus.Purged) return 0;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        long lastItemId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<(long ItemId, long MessageId, string EmlRel)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
SELECT id, message_id, eml_rel_path
FROM cleanup_items
WHERE job_id = @jobId AND id > @lastId
ORDER BY id
LIMIT 200;
""";
                select.Parameters.AddWithValue("@jobId", jobId);
                select.Parameters.AddWithValue("@lastId", lastItemId);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add((
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                }
            }

            if (items.Count == 0) break;

            foreach (var item in items)
            {
                lastItemId = item.ItemId;
                DeleteDirectory(Path.Combine(QuarantineRoot, jobId.ToString(CultureInfo.InvariantCulture),
                    item.MessageId.ToString(CultureInfo.InvariantCulture)));
                if (item.EmlRel.Length > 0)
                {
                    DeleteDirectory(Path.Combine(_paths.EmlDirectory,
                        item.EmlRel.Replace('/', Path.DirectorySeparatorChar)));
                }
            }
        }

        DeleteDirectory(Path.Combine(QuarantineRoot, jobId.ToString(CultureInfo.InvariantCulture)));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
UPDATE cleanup_items SET payload_json = '' WHERE job_id = @jobId;
UPDATE cleanup_jobs
SET status = @status, freed_bytes = total_bytes, finished_utc = @finished
WHERE id = @id;
""";
            command.Parameters.AddWithValue("@jobId", jobId);
            command.Parameters.AddWithValue("@status", (int)CleanupStatus.Purged);
            command.Parameters.AddWithValue("@finished", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@id", jobId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return job.TotalBytes;
    }

    public async Task<long> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, settings.CleanupQuarantineDays));

        var ids = new List<long>();
        await using (var connection = await _factory.CreateAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT id FROM cleanup_jobs
WHERE status IN (@completed, @cancelled) AND started_utc < @cutoff
ORDER BY id;
""";
            command.Parameters.AddWithValue("@completed", (int)CleanupStatus.Completed);
            command.Parameters.AddWithValue("@cancelled", (int)CleanupStatus.Cancelled);
            command.Parameters.AddWithValue("@cutoff", cutoff.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        long freed = 0;
        foreach (var id in ids)
        {
            freed += await PurgeAsync(id, ct).ConfigureAwait(false);
        }
        return freed;
    }

    // ---------------- 任务查询 ----------------

    public async Task<IReadOnlyList<CleanupJob>> GetJobsAsync(int take = 30, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, mode, status, filter_json, total_count, processed_count,
       total_bytes, freed_bytes, started_utc, finished_utc, COALESCE(note, '')
FROM cleanup_jobs
ORDER BY id DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 200));
        var jobs = new List<CleanupJob>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            jobs.Add(MapJob(reader));
        }
        return jobs;
    }

    private async Task<CleanupJob?> GetJobAsync(long jobId, CancellationToken ct)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, mode, status, filter_json, total_count, processed_count,
       total_bytes, freed_bytes, started_utc, finished_utc, COALESCE(note, '')
FROM cleanup_jobs WHERE id = @id;
""";
        command.Parameters.AddWithValue("@id", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    private static CleanupJob MapJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Mode = (CleanupMode)reader.GetInt32(1),
        Status = (CleanupStatus)reader.GetInt32(2),
        FilterJson = reader.GetString(3),
        TotalCount = reader.GetInt32(4),
        ProcessedCount = reader.GetInt32(5),
        TotalBytes = reader.GetInt64(6),
        FreedBytes = reader.GetInt64(7),
        StartedUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        FinishedUtc = reader.IsDBNull(9)
            ? null
            : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
        Note = reader.GetString(10)
    };

    // ---------------- 任务写入 ----------------

    private static async Task<long> InsertJobAsync(SqliteConnection connection, CleanupJob job, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO cleanup_jobs(mode, status, filter_json, total_count, processed_count,
                         total_bytes, freed_bytes, started_utc)
VALUES(@mode, @status, @filter, @total, @processed, @bytes, 0, @started);
SELECT last_insert_rowid();
""";
        command.Parameters.AddWithValue("@mode", (int)job.Mode);
        command.Parameters.AddWithValue("@status", (int)job.Status);
        command.Parameters.AddWithValue("@filter", job.FilterJson);
        command.Parameters.AddWithValue("@total", job.TotalCount);
        command.Parameters.AddWithValue("@processed", job.ProcessedCount);
        command.Parameters.AddWithValue("@bytes", job.TotalBytes);
        command.Parameters.AddWithValue("@started", job.StartedUtc.ToUniversalTime()
            .ToString("o", CultureInfo.InvariantCulture));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    private static async Task UpdateProgressAsync(
        SqliteConnection connection, long jobId, int processed, long bytes, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE cleanup_jobs SET processed_count = @processed, total_bytes = @bytes WHERE id = @id;
""";
        command.Parameters.AddWithValue("@processed", processed);
        command.Parameters.AddWithValue("@bytes", bytes);
        command.Parameters.AddWithValue("@id", jobId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task FinishJobAsync(SqliteConnection connection, CleanupJob job, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE cleanup_jobs
SET status = @status, processed_count = @processed, total_bytes = @bytes,
    finished_utc = @finished, note = @note
WHERE id = @id;
""";
        command.Parameters.AddWithValue("@status", (int)job.Status);
        command.Parameters.AddWithValue("@processed", job.ProcessedCount);
        command.Parameters.AddWithValue("@bytes", job.TotalBytes);
        command.Parameters.AddWithValue("@finished", (job.FinishedUtc ?? DateTimeOffset.UtcNow)
            .ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@note", job.Note);
        command.Parameters.AddWithValue("@id", job.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection, CleanupFilter filter, CancellationToken ct)
    {
        var (joins, where, prms) = BuildWhere(filter, excludeJobId: 0);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM messages m {joins} WHERE {where};";
        Apply(command, prms);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    // ---------------- 筛选条件 ----------------

    private static (string Joins, string Where, List<(string Name, object? Value)> Params) BuildWhere(
        CleanupFilter filter, long excludeJobId)
    {
        var joins = "JOIN folders f ON f.id = m.folder_id";
        var conditions = new List<string>();
        var prms = new List<(string, object?)>();

        if (filter.AccountId.HasValue)
        {
            conditions.Add("m.account_id = @accountId");
            prms.Add(("@accountId", filter.AccountId.Value));
        }
        if (filter.FolderId.HasValue)
        {
            conditions.Add("m.folder_id = @folderId");
            prms.Add(("@folderId", filter.FolderId.Value));
        }

        // 排除停用账户：停止使用只暂停收发，其本地邮件必须保持可读，不能被自动清理删除。
        if (filter.ExcludeAccountIds.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < filter.ExcludeAccountIds.Count; i++)
            {
                var name = "@excluded" + i;
                names.Add(name);
                prms.Add((name, filter.ExcludeAccountIds[i]));
            }

            conditions.Add("m.account_id NOT IN (" + string.Join(",", names) + ")");
        }

        var kinds = new List<int>();
        if (filter.IncludeInbox) kinds.Add((int)FolderKind.Inbox);
        if (filter.IncludeSent) kinds.Add((int)FolderKind.Sent);
        if (filter.IncludeTrash) kinds.Add((int)FolderKind.Trash);
        if (filter.IncludeJunk) kinds.Add((int)FolderKind.Junk);
        if (filter.IncludeArchive) kinds.Add((int)FolderKind.Archive);

        // 未选择任何分类时不返回任何邮件，避免误清理整库
        conditions.Add(kinds.Count == 0
            ? "1 = 0"
            : $"f.kind IN ({string.Join(',', kinds)})");

        // 草稿永远不参与清理
        conditions.Add("(m.flags & 8) = 0");

        if (filter.ProtectStarred) conditions.Add("(m.flags & 4) = 0");
        if (filter.ProtectUnread) conditions.Add("(m.flags & 1) <> 0");

        if (filter.BeforeUtc is { } before)
        {
            conditions.Add("m.date_utc < @before");
            prms.Add(("@before", before.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }
        if (filter.AfterUtc is { } after)
        {
            conditions.Add("m.date_utc > @after");
            prms.Add(("@after", after.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }
        if (filter.MinSizeBytes > 0)
        {
            conditions.Add("m.size >= @minSize");
            prms.Add(("@minSize", filter.MinSizeBytes));
        }
        if (filter.MaxSizeBytes > 0)
        {
            conditions.Add("m.size <= @maxSize");
            prms.Add(("@maxSize", filter.MaxSizeBytes));
        }

        var extensions = (filter.Extensions ?? new List<string>())
            .Select(NormalizeExtension)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        if (extensions.Count > 0)
        {
            var checks = new List<string>();
            for (var i = 0; i < extensions.Count; i++)
            {
                var name = "@ext" + i;
                checks.Add($"LOWER(a.file_name) LIKE {name}");
                prms.Add((name, "%." + extensions[i]));
            }
            conditions.Add(
                "EXISTS (SELECT 1 FROM attachments a WHERE a.message_id = m.id " +
                "AND a.is_inline = 0 AND (" + string.Join(" OR ", checks) + "))");
        }
        else if (filter.OnlyWithAttachments)
        {
            conditions.Add("EXISTS (SELECT 1 FROM attachments a " +
                           "WHERE a.message_id = m.id AND a.is_inline = 0)");
        }

        if (excludeJobId > 0)
        {
            conditions.Add(
                "m.id NOT IN (SELECT message_id FROM cleanup_items WHERE job_id = @excludeJobId)");
            prms.Add(("@excludeJobId", excludeJobId));
        }

        return (joins, string.Join(" AND ", conditions), prms);
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (value.Length is 0 or > 12) return string.Empty;
        return value.All(c => char.IsLetterOrDigit(c) || c == '-') ? value : string.Empty;
    }

    // ---------------- 文件隔离 ----------------

    private void MoveToQuarantine(long jobId, long accountId, long messageId)
    {
        try
        {
            var source = Path.Combine(
                _paths.EmlDirectory,
                accountId.ToString(CultureInfo.InvariantCulture),
                messageId.ToString(CultureInfo.InvariantCulture));
            if (!Directory.Exists(source)) return;

            var target = Path.Combine(
                QuarantineRoot,
                jobId.ToString(CultureInfo.InvariantCulture),
                messageId.ToString(CultureInfo.InvariantCulture));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(source, target);
        }
        catch (Exception ex)
        {
            // 文件被占用时保留在原位；purge 仍会按原路径删除
            _logger.LogWarning(ex, "移动邮件 {MessageId} 附件目录到隔离区失败", messageId);
        }
    }

    private void MoveFromQuarantine(long jobId, long messageId, string emlRel)
    {
        if (string.IsNullOrEmpty(emlRel)) return;
        try
        {
            var source = Path.Combine(
                QuarantineRoot,
                jobId.ToString(CultureInfo.InvariantCulture),
                messageId.ToString(CultureInfo.InvariantCulture));
            if (!Directory.Exists(source)) return;

            var target = Path.Combine(_paths.EmlDirectory, emlRel.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(source, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从隔离区恢复邮件 {MessageId} 附件目录失败", messageId);
        }
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除隔离目录失败：{Path}", path);
        }
    }

    // ---------------- 参数辅助 ----------------

    private static string Placeholders(int count) =>
        string.Join(',', Enumerable.Range(0, count).Select(i => "@id" + i));

    private static void AddIdParameters(SqliteCommand command, IReadOnlyList<long> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue("@id" + i, ids[i]);
        }
    }

    private static void Apply(SqliteCommand command, List<(string Name, object? Value)> prms)
    {
        foreach (var (name, value) in prms)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private async Task<AppSettings> GetSettingsAsync(CancellationToken ct)
    {
        // 仅用于读取保留天数；ISettingsStore 未注入以避免与设置窗口产生循环依赖
        var path = _paths.SettingsPath;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取设置失败，使用默认清理保留天数");
            return new AppSettings();
        }
    }

    /// <summary>审计表里保存的完整任务参数，支持中断后按原条件恢复。</summary>
    private sealed class JobPayload
    {
        public CleanupFilter Filter { get; set; } = new();
        public List<long>? SelectedIds { get; set; }
    }
}
