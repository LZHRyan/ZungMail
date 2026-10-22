using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>联系人导入 / 导出：写库去重、名称刷新、导出文件内容。</summary>
[Trait("Category", "Integration")]
public sealed class ContactExchangeTests : IDisposable
{
    private readonly string _root;

    public ContactExchangeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-contacts-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public async Task 导入_写入联系人_重复导入只刷新姓名不累加次数()
    {
        var service = CreateService(out var repository);
        var vcf = Path.Combine(_root, "contacts.vcf");
        await File.WriteAllTextAsync(vcf, """
BEGIN:VCARD
VERSION:3.0
FN:张三
EMAIL:zhang@example.com
END:VCARD
BEGIN:VCARD
VERSION:3.0
FN:李四
EMAIL:lisi@example.com
END:VCARD
""");

        var first = await service.ImportAsync(vcf);

        Assert.Equal(2, first.Count);
        var contacts = await repository.GetAllAsync();
        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, c => Assert.Equal(0, c.UseCount));

        // 再次导入并把「张三」改名：不新增行、使用次数不累加、名称被刷新。
        await File.WriteAllTextAsync(vcf, """
BEGIN:VCARD
VERSION:3.0
FN:张三（货代）
EMAIL:zhang@example.com
END:VCARD
""");
        var second = await service.ImportAsync(vcf);

        Assert.Equal(1, second.Count);
        contacts = await repository.GetAllAsync();
        Assert.Equal(2, contacts.Count);
        var zhang = Assert.Single(contacts, c => c.Email == "zhang@example.com");
        Assert.Equal("张三（货代）", zhang.Name);
        Assert.Equal(0, zhang.UseCount);
    }

    [Fact]
    public async Task 导出_vCard与CSV包含全部联系人()
    {
        var service = CreateService(out var repository);
        await repository.UpsertManyAsync(new[]
        {
            new Contact { Email = "a@example.com", Name = "甲" },
            new Contact { Email = "b@example.com", Name = "乙" }
        });

        var vcfPath = Path.Combine(_root, "out.vcf");
        var csvPath = Path.Combine(_root, "out.csv");

        var vcfResult = await service.ExportAsync(vcfPath);
        var csvResult = await service.ExportAsync(csvPath);

        Assert.Equal(2, vcfResult.Count);
        Assert.Equal(2, csvResult.Count);

        var vcf = await File.ReadAllTextAsync(vcfPath);
        Assert.Contains("EMAIL;TYPE=INTERNET:a@example.com", vcf);
        Assert.Contains("EMAIL;TYPE=INTERNET:b@example.com", vcf);

        var csv = await File.ReadAllTextAsync(csvPath);
        Assert.StartsWith("姓名,邮箱", csv);
        Assert.Contains("a@example.com", csv);

        // 导出的 vCard 能被自己的解析器读回（往返一致）。
        var entries = ContactFormats.Parse(vcf, ContactFileFormat.VCard);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task 导入_空文件给出可读提示()
    {
        var service = CreateService(out _);
        var path = Path.Combine(_root, "empty.csv");
        await File.WriteAllTextAsync(path, "姓名,邮箱\r\n");

        var result = await service.ImportAsync(path);

        Assert.Equal(-1, result.Count);
        Assert.Contains("未解析到联系人", result.Message);
    }

    [Fact]
    public async Task 分页查询_页大小与总数正确_搜索命中数不受分页影响()
    {
        _ = CreateService(out var repository);

        var contacts = Enumerable.Range(1, 120)
            .Select(i => new Contact { Email = $"user{i:D3}@example.com", Name = $"联系人{i:D3}" })
            .ToArray();
        await repository.UpsertManyAsync(contacts);

        var first = await repository.QueryAsync(filter: null, skip: 0, take: 50);
        Assert.Equal(120, first.TotalCount);
        Assert.Equal(50, first.Items.Count);
        Assert.True(first.HasMore);

        var third = await repository.QueryAsync(filter: null, skip: 100, take: 50);
        Assert.Equal(120, third.TotalCount);
        Assert.Equal(20, third.Items.Count);
        Assert.False(third.HasMore);

        // 搜索仍按整表统计命中数：user010..user019 共 10 位
        var matched = await repository.QueryAsync("user01", skip: 0, take: 50);
        Assert.Equal(10, matched.TotalCount);
        Assert.All(matched.Items, contact => Assert.Contains("user01", contact.Email, StringComparison.Ordinal));
    }

    private ContactExchangeService CreateService(out SqliteContactRepository repository)
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();

        repository = new SqliteContactRepository(factory);
        return new ContactExchangeService(repository, NullLogger<ContactExchangeService>.Instance);
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
}
