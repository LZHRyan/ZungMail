using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 一票货案件归并与时效看板：同一票货的多封邮件必须落到一个案件上，
/// 先到提单后到 SO 时要合并并升级主键，关闭案件后不再进看板。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShipmentRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteShipmentRepository _shipments;
    private readonly SqliteMailReferenceRepository _references;

    public ShipmentRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-ship-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        _shipments = new SqliteShipmentRepository(_factory);
        _references = new SqliteMailReferenceRepository(_factory);
        SeedAccountAndFolder();
    }

    private void SeedAccountAndFolder()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 0);
""";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }

    private Task<long> AddMessageAsync(uint uid, string subject, DateTimeOffset date)
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            Subject = subject,
            FromEmail = "ops@carrier.example",
            FromName = "Carrier Ops",
            DateUtc = date
        });

    private static FreightReference Ref(FreightReferenceKind kind, string value) => new()
    {
        Kind = kind,
        Value = value,
        Normalized = FreightReference.Normalize(value),
        Source = ReferenceSource.Subject
    };

    private static FreightDeadline Deadline(FreightDeadlineKind kind, DateTimeOffset due) => new()
    {
        Kind = kind,
        DueUtc = due,
        RawText = FreightDeadline.LabelOf(kind) + " 测试",
        Source = ReferenceSource.Body
    };

    [Fact]
    public async Task 邮件类型推进案件阶段_异常只打标记()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var booking = await AddMessageAsync(1, "Booking Confirmation SO No.: SHA1234567", date);
        var si = await AddMessageAsync(2, "SI for SO SHA1234567", date.AddHours(1));
        var rollover = await AddMessageAsync(3, "甩柜通知 SO SHA1234567", date.AddHours(2));

        // 生产链路上类型由标识索引先写库，案件归并再读同一批数据；这里保持一致
        await _references.ReplaceRangeAsync(new[]
        {
            new ReferenceIndexEntry(booking, false, Array.Empty<FreightReference>(), MailType.BookingConfirmation, 6),
            new ReferenceIndexEntry(si, false, Array.Empty<FreightReference>(), MailType.ShippingInstruction, 6),
            new ReferenceIndexEntry(rollover, false, Array.Empty<FreightReference>(), MailType.Rollover, 6)
        });

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = booking,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                MailType = MailType.BookingConfirmation
            },
            new ShipmentIndexEntry
            {
                EmailId = si,
                MessageDate = date.AddHours(1),
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                MailType = MailType.ShippingInstruction
            },
            new ShipmentIndexEntry
            {
                EmailId = rollover,
                MessageDate = date.AddHours(2),
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                MailType = MailType.Rollover
            }
        });

        var summary = Assert.Single(await _shipments.GetRecentCasesAsync());
        Assert.Equal(CaseStage.SiReceived, summary.Stage);
        Assert.Equal("已补料", summary.StageText);
        Assert.True(summary.HasException);
        Assert.Equal("有异常", summary.ExceptionText);

        var detail = await _shipments.GetCaseAsync(summary.Id);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Messages, m => m.MailType == MailType.ShippingInstruction);
        Assert.Contains(detail.Messages, m => m.IsException);
    }

    [Fact]
    public async Task 同一订舱号的两封邮件_归入同一案件()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var first = await AddMessageAsync(1, "SO No.: SHA1234567", date);
        var second = await AddMessageAsync(2, "RE: SO No.: SHA1234567", date.AddHours(2));

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = first,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") }
            },
            new ShipmentIndexEntry
            {
                EmailId = second,
                MessageDate = date.AddHours(2),
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") }
            }
        });

        var cases = await _shipments.GetRecentCasesAsync();
        var single = Assert.Single(cases);
        Assert.Equal(FreightReferenceKind.Booking, single.PrimaryKind);
        Assert.Equal("SHA1234567", single.PrimaryRef);
        Assert.Equal(2, single.MessageCount);

        Assert.Equal(single.Id, await _shipments.GetCaseIdForMessageAsync(first));
        Assert.Equal(single.Id, await _shipments.GetCaseIdForMessageAsync(second));
    }

    [Fact]
    public async Task 先提单后订舱_合并为一个案件并升级主键()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-2);
        var bl = await AddMessageAsync(1, "Draft B/L HBL99887766", date);
        var so = await AddMessageAsync(2, "SO No.: SHA1234567 / HBL99887766", date.AddDays(1));

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = bl,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.BillOfLading, "HBL99887766") }
            }
        });

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = so,
                MessageDate = date.AddDays(1),
                References = new[]
                {
                    Ref(FreightReferenceKind.Booking, "SHA1234567"),
                    Ref(FreightReferenceKind.BillOfLading, "HBL99887766")
                }
            }
        });

        var cases = await _shipments.GetRecentCasesAsync();
        var single = Assert.Single(cases);
        Assert.Equal(FreightReferenceKind.Booking, single.PrimaryKind);
        Assert.Equal("SHA1234567", single.PrimaryRef);
        Assert.Equal(2, single.MessageCount);
    }

    [Fact]
    public async Task 只有箱号相同_也视为一票货()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var first = await AddMessageAsync(1, "VGM submission TGHU1234567", date);
        var second = await AddMessageAsync(2, "装柜照片 TGHU1234567", date.AddHours(1));

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = first,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Container, "TGHU1234567") }
            },
            new ShipmentIndexEntry
            {
                EmailId = second,
                MessageDate = date.AddHours(1),
                References = new[] { Ref(FreightReferenceKind.Container, "TGHU1234567") }
            }
        });

        var cases = await _shipments.GetRecentCasesAsync();
        var single = Assert.Single(cases);
        Assert.Equal(2, single.MessageCount);

        var detail = await _shipments.GetCaseAsync(single.Id);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count);
    }

    [Fact]
    public async Task 时效看板_按到期时间返回并带出案件号()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var email = await AddMessageAsync(1, "SO No.: SHA1234567", date);
        var soon = DateTimeOffset.UtcNow.AddHours(3);
        var later = DateTimeOffset.UtcNow.AddHours(20);

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = email,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                Deadlines = new[]
                {
                    Deadline(FreightDeadlineKind.CutOff, later),
                    Deadline(FreightDeadlineKind.SiCutOff, soon)
                }
            }
        });

        var board = await _shipments.GetUpcomingDeadlinesAsync(10);
        Assert.Equal(2, board.Count);
        Assert.Equal(FreightDeadlineKind.SiCutOff, board[0].Kind);
        Assert.Equal("SHA1234567", board[0].CaseRef);
        Assert.True(board[0].CaseId > 0);
        Assert.Contains("截补料", board[0].KindLabel);

        var forMessage = await _shipments.GetDeadlinesForMessageAsync(email);
        Assert.Equal(2, forMessage.Count);
    }

    [Fact]
    public async Task 关闭案件后_不再进入时效看板()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var email = await AddMessageAsync(1, "SO No.: SHA1234567", date);

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = email,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                Deadlines = new[] { Deadline(FreightDeadlineKind.CutOff, DateTimeOffset.UtcNow.AddHours(4)) }
            }
        });

        var caseId = await _shipments.GetCaseIdForMessageAsync(email);
        Assert.Single(await _shipments.GetUpcomingDeadlinesAsync(10));

        await _shipments.SetCaseStatusAsync(caseId, ShipmentCaseStatus.Closed);

        Assert.Empty(await _shipments.GetUpcomingDeadlinesAsync(10));
        var summary = Assert.Single(await _shipments.GetRecentCasesAsync());
        Assert.True(summary.IsClosed);
        Assert.Equal("已关闭", summary.StatusText);
    }

    [Fact]
    public async Task 导出资料包_带出邮件与附件路径()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var email = await AddMessageAsync(1, "Draft B/L HBL99887766", date);
        await _messages.SaveBodyAsync(email, "<p>草稿见附件</p>", "草稿见附件", new[]
        {
            new AttachmentInfo { FileName = "BL-DRAFT.pdf", ContentType = "application/pdf", Size = 2048 }
        });

        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE attachments SET local_path = @path WHERE message_id = @id;";
            command.Parameters.AddWithValue("@path", @"C:\data\BL-DRAFT.pdf");
            command.Parameters.AddWithValue("@id", email);
            command.ExecuteNonQuery();
        }

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = email,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.BillOfLading, "HBL99887766") }
            }
        });

        var caseId = await _shipments.GetCaseIdForMessageAsync(email);
        var export = await _shipments.GetCaseExportAsync(caseId);

        Assert.NotNull(export);
        var message = Assert.Single(export!.Messages);
        Assert.Equal("Draft B/L HBL99887766", message.Subject);
        Assert.Equal("Carrier Ops", message.FromDisplay);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("BL-DRAFT.pdf", attachment.FileName);
        Assert.Equal(@"C:\data\BL-DRAFT.pdf", attachment.LocalPath);
    }

    [Fact]
    public async Task 重扫覆盖旧节点_删除邮件后案件自动清理()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        var email = await AddMessageAsync(1, "SO No.: SHA1234567", date);

        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = email,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                Deadlines = new[] { Deadline(FreightDeadlineKind.CutOff, DateTimeOffset.UtcNow.AddHours(2)) }
            }
        });
        Assert.Single(await _shipments.GetDeadlinesForMessageAsync(email));

        // 第二次扫描没有节点（正文变化）→ 旧节点必须被清掉
        await _shipments.ApplyAsync(new[]
        {
            new ShipmentIndexEntry
            {
                EmailId = email,
                MessageDate = date,
                References = new[] { Ref(FreightReferenceKind.Booking, "SHA1234567") },
                Deadlines = Array.Empty<FreightDeadline>()
            }
        });
        Assert.Empty(await _shipments.GetDeadlinesForMessageAsync(email));

        await _messages.DeleteAsync(email);

        Assert.Equal(0, await _shipments.GetCaseIdForMessageAsync(email));
        Assert.Empty(await _shipments.GetRecentCasesAsync());
    }
}
