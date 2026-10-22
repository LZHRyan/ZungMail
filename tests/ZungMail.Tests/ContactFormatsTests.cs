using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>联系人 vCard / CSV 解析与生成。</summary>
[Trait("Category", "Unit")]
public class ContactFormatsTests
{
    [Fact]
    public void VCard_解析姓名邮箱与折行()
    {
        const string vcf = """
BEGIN:VCARD
VERSION:3.0
N:张;三;;;
FN:张三
EMAIL;TYPE=INTERNET:Zhang.San@Example.com
END:VCARD
BEGIN:VCARD
VERSION:3.0
FN:李四
EMAIL:lisi@example.com
NOTE:这一行故意写得很长，用来验证
 折行续行会被拼回同一行
END:VCARD
""";

        var entries = ContactFormats.Parse(vcf, ContactFileFormat.VCard);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Email == "zhang.san@example.com" && e.Name == "张三");
        Assert.Contains(entries, e => e.Email == "lisi@example.com" && e.Name == "李四");
    }

    [Fact]
    public void VCard_N兜底与mailto前缀()
    {
        const string vcf = """
BEGIN:VCARD
VERSION:3.0
N:Wang;Xiao Ming;;;
EMAIL;TYPE=INTERNET:mailto:wxm@example.com
END:VCARD
""";

        var entry = Assert.Single(ContactFormats.Parse(vcf, ContactFileFormat.VCard));
        Assert.Equal("wxm@example.com", entry.Email);
        Assert.Equal("Wang Xiao Ming", entry.Name);
    }

    [Fact]
    public void VCard_缺少邮箱的卡片被跳过且QUOTED_PRINTABLE可解码()
    {
        const string vcf = """
BEGIN:VCARD
VERSION:3.0
FN:没有邮箱的人
TEL:123456
END:VCARD
BEGIN:VCARD
VERSION:3.0
FN:=E6=9D=8E=E5=9B=9B
EMAIL;ENCODING=QUOTED-PRINTABLE:lisi@example.com
END:VCARD
""";

        var entry = Assert.Single(ContactFormats.Parse(vcf, ContactFileFormat.VCard));
        Assert.Equal("lisi@example.com", entry.Email);
        Assert.Equal("李四", entry.Name);
    }

    [Theory]
    [InlineData("姓名,邮箱\r\n张三,zhang@example.com\r\n")]
    [InlineData("Name,E-mail\r\n张三,zhang@example.com\r\n")]
    [InlineData("邮箱,姓名\ntest@example.com,张三\n")]
    public void Csv_识别中英文表头(string csv)
    {
        var entries = ContactFormats.Parse(csv, ContactFileFormat.Csv);

        Assert.Contains(entries, e => e.Email == "zhang@example.com" || e.Email == "test@example.com");
        Assert.All(entries, e => Assert.Equal("张三", e.Name));
    }

    [Fact]
    public void Csv_无表头按邮箱姓名列序并处理引号()
    {
        const string csv = "zhang@example.com,\"张三, 货代\"\r\ninvalid-line\r\nlisi@example.com,李四\r\n";

        var entries = ContactFormats.Parse(csv, ContactFileFormat.Csv);

        Assert.Equal(2, entries.Count);
        Assert.Equal("张三, 货代", entries[0].Name);
        Assert.Equal("lisi@example.com", entries[1].Email);
    }

    [Fact]
    public void VCard_导出后可原样读回()
    {
        var contacts = new[]
        {
            new Contact { Email = "a@example.com", Name = "张三, 经理" },
            new Contact { Email = "b@example.com", Name = "Li;Si" }
        };

        var vcf = ContactFormats.Write(contacts, ContactFileFormat.VCard);
        var entries = ContactFormats.Parse(vcf, ContactFileFormat.VCard);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Email == "a@example.com" && e.Name == "张三, 经理");
        Assert.Contains(entries, e => e.Email == "b@example.com" && e.Name == "Li;Si");
    }

    [Fact]
    public void Csv_导出后可原样读回()
    {
        var contacts = new[]
        {
            new Contact { Email = "a@example.com", Name = "张三", UseCount = 7, IsWhitelisted = true }
        };

        var csv = ContactFormats.Write(contacts, ContactFileFormat.Csv);
        Assert.StartsWith("姓名,邮箱", csv);

        var entries = ContactFormats.Parse(csv, ContactFileFormat.Csv);
        var entry = Assert.Single(entries);
        Assert.Equal("a@example.com", entry.Email);
        Assert.Equal("张三", entry.Name);
    }

    [Fact]
    public void 按扩展名识别格式()
    {
        Assert.Equal(ContactFileFormat.Csv, ContactFormats.DetectFormat("a.CSV"));
        Assert.Equal(ContactFileFormat.VCard, ContactFormats.DetectFormat("a.vcf"));
        Assert.Equal(ContactFileFormat.VCard, ContactFormats.DetectFormat(null));
    }
}
