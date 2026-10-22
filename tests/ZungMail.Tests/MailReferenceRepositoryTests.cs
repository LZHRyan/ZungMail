using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>业务标识仓储：写入、聚合、补扫状态与结构化搜索联动。</summary>
[Trait("Category", "Integration")]
public sealed class MailReferenceRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteMailReferenceRepository _references;

    public MailReferenceRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-refs-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
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

    private Task<long> AddMessageAsync(uint uid, string subject, string preview = "", string body = "", bool hasAttachment = false)
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            Subject = subject,
            Preview = preview,
            BodyText = body,
            BodyState = string.IsNullOrEmpty(body) ? BodyState.NotDownloaded : BodyState.Downloaded,
            HasAttachment = hasAttachment,
            FromEmail = "ops@carrier.example",
            DateUtc = DateTimeOffset.UtcNow.AddMinutes(-uid)
        });

    private static FreightReference Ref(FreightReferenceKind kind, string value) => new()
    {
        Kind = kind,
        Value = value,
        Normalized = FreightReference.Normalize(value),
        Source = ReferenceSource.Subject
    };

    [Fact]
    public async Task 写入后回读_按业务优先级排序()
    {
        var id = await AddMessageAsync(1, "Booking confirmation");

        await _references.ReplaceForMessageAsync(id, new[]
        {
            Ref(FreightReferenceKind.Container, "TGHU1234565"),
            Ref(FreightReferenceKind.Booking, "SO-2026-001")
        }, bodyIndexed: false);

        var refs = await _references.GetForMessageAsync(id);
        Assert.Equal(2, refs.Count);
        Assert.Equal(FreightReferenceKind.Booking, refs[0].Kind);
        Assert.Equal(FreightReferenceKind.Container, refs[1].Kind);
    }

    [Fact]
    public async Task 批量回读_只返回请求的邮件()
    {
        var first = await AddMessageAsync(1, "SO 12345678");
        var second = await AddMessageAsync(2, "SO 12345678");

        await _references.ReplaceRangeAsync(new[]
        {
            new ReferenceIndexEntry(first, false, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") }),
            new ReferenceIndexEntry(second, false, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") })
        });

        var map = await _references.GetForMessagesAsync(new[] { first, second });
        Assert.Equal(2, map.Count);
        Assert.All(map.Values, list => Assert.Single(list));
    }

    [Fact]
    public async Task 聚合_同一单号跨邮件计数并按最近时间排序()
    {
        var first = await AddMessageAsync(1, "SO 12345678");
        var second = await AddMessageAsync(2, "RE: SO 12345678");
        var third = await AddMessageAsync(3, "另一票");

        await _references.ReplaceRangeAsync(new[]
        {
            new ReferenceIndexEntry(first, false, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") }),
            new ReferenceIndexEntry(second, false, new[] { Ref(FreightReferenceKind.Booking, "SO 2026 001") }),
            new ReferenceIndexEntry(third, false, new[] { Ref(FreightReferenceKind.BillOfLading, "HBL99887766") })
        });

        var top = await _references.GetTopAsync(take: 10);
        Assert.Equal(2, top.Count);

        var booking = Assert.Single(top, r => r.Kind == FreightReferenceKind.Booking);
        Assert.Equal(2, booking.MessageCount);
        Assert.Equal("SO2026001", booking.Normalized);
        Assert.StartsWith("booking:", booking.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 补扫_正文到达后才重新进入队列()
    {
        var id = await AddMessageAsync(1, "Booking confirmation");

        var pending = await _references.GetPendingScansAsync();
        Assert.Contains(pending, row => row.EmailId == id);

        await _references.ReplaceForMessageAsync(id, Array.Empty<FreightReference>(), bodyIndexed: false);
        pending = await _references.GetPendingScansAsync();
        Assert.DoesNotContain(pending, row => row.EmailId == id);

        await _messages.SaveBodyAsync(id, "<p>SO No.: SO-2026-001</p>", "SO No.: SO-2026-001", Array.Empty<AttachmentInfo>());
        await _references.InvalidateAsync(id);

        pending = await _references.GetPendingScansAsync();
        var row = Assert.Single(pending, p => p.EmailId == id);
        Assert.True(row.HasBody);

        await _references.ReplaceForMessageAsync(id, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") }, bodyIndexed: true);
        Assert.DoesNotContain(await _references.GetPendingScansAsync(), p => p.EmailId == id);
        Assert.Single(await _references.GetForMessageAsync(id));
    }

    [Fact]
    public async Task 删除邮件_级联清理标识()
    {
        var id = await AddMessageAsync(1, "SO 12345678");
        await _references.ReplaceForMessageAsync(id, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") }, bodyIndexed: false);

        await _messages.DeleteAsync(id);

        Assert.Empty(await _references.GetForMessageAsync(id));
        Assert.DoesNotContain(await _references.GetPendingScansAsync(), row => row.EmailId == id);
    }

    [Fact]
    public async Task 搜索_按业务编号过滤邮件()
    {
        var bookingMessage = await AddMessageAsync(1, "Booking confirmation SO-2026-001");
        var otherMessage = await AddMessageAsync(2, "Invoice only");

        await _references.ReplaceRangeAsync(new[]
        {
            new ReferenceIndexEntry(bookingMessage, false, new[] { Ref(FreightReferenceKind.Booking, "SO-2026-001") }),
            new ReferenceIndexEntry(otherMessage, false, new[] { Ref(FreightReferenceKind.Invoice, "INV-2026-009") })
        });

        var byBooking = await _messages.QueryAsync(new MessageQuery { SearchText = "booking:SO2026" });
        Assert.Equal(1, byBooking.TotalCount);
        Assert.Equal(bookingMessage, Assert.Single(byBooking.Items).Id);

        var withHyphens = await _messages.QueryAsync(new MessageQuery { SearchText = "booking:SO-2026-001" });
        Assert.Equal(1, withHyphens.TotalCount);

        var noMatch = await _messages.QueryAsync(new MessageQuery { SearchText = "bl:HBL99887766" });
        Assert.Equal(0, noMatch.TotalCount);

        var anyKind = await _messages.QueryAsync(new MessageQuery { SearchText = "ref:INV2026" });
        Assert.Equal(1, anyKind.TotalCount);
        Assert.Equal(otherMessage, Assert.Single(anyKind.Items).Id);

        // 运算符与自由文本是 AND 关系
        var combined = await _messages.QueryAsync(new MessageQuery { SearchText = "booking:SO2026 invoice" });
        Assert.Equal(0, combined.TotalCount);
    }

    [Fact]
    public async Task 搜索_按行业邮件类型过滤()
    {
        var si = await AddMessageAsync(1, "SI CUT-OFF 2026-03-12");
        var personal = await AddMessageAsync(2, "周报");

        await _references.ReplaceRangeAsync(new[]
        {
            new ReferenceIndexEntry(si, false, Array.Empty<FreightReference>(), MailType.ShippingInstruction, 6),
            new ReferenceIndexEntry(personal, false, Array.Empty<FreightReference>())
        });

        var byKeyword = await _messages.QueryAsync(new MessageQuery { SearchText = "type:si" });
        Assert.Equal(1, byKeyword.TotalCount);
        Assert.Equal(si, Assert.Single(byKeyword.Items).Id);

        var byChinese = await _messages.QueryAsync(new MessageQuery { SearchText = "type:补料" });
        Assert.Equal(1, byChinese.TotalCount);

        var none = await _messages.QueryAsync(new MessageQuery { SearchText = "type:vgm" });
        Assert.Equal(0, none.TotalCount);
    }

    [Fact]
    public async Task 搜索_附件与未读运算符()
    {
        var withAttachment = await AddMessageAsync(1, "提单草稿", hasAttachment: true);
        await _messages.SaveBodyAsync(withAttachment, "<p>正文</p>", "正文", new[]
        {
            new AttachmentInfo { FileName = "BL-DRAFT.pdf", ContentType = "application/pdf", Size = 1024 }
        });

        await AddMessageAsync(2, "报价");

        var query = await _messages.QueryAsync(new MessageQuery { SearchText = "has:attachment is:unread" });
        Assert.Equal(1, query.TotalCount);
        Assert.Equal(withAttachment, Assert.Single(query.Items).Id);
    }
}
