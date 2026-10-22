using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>联系人导入 / 导出结果。</summary>
public sealed record ContactExchangeResult(int Count, string Message)
{
    public bool Success => Count >= 0;
}

/// <summary>
/// 地址簿导入导出（vCard / CSV）：面向 Foxmail、Outlook、手机通讯录的常见格式。
/// 导入走 <see cref="IContactRepository.UpsertManyAsync"/>，不会把导入算作一次往来。
/// </summary>
public sealed class ContactExchangeService
{
    private readonly IContactRepository _contacts;
    private readonly ILogger<ContactExchangeService> _logger;

    public ContactExchangeService(IContactRepository contacts, ILogger<ContactExchangeService> logger)
    {
        _contacts = contacts;
        _logger = logger;
    }

    public async Task<ContactExchangeResult> ImportAsync(
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ContactExchangeResult(-1, "文件不存在：" + path);
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            var entries = ContactFormats.Parse(text, ContactFormats.DetectFormat(path));
            if (entries.Count == 0)
            {
                return new ContactExchangeResult(-1, "未解析到联系人，请确认文件为 vCard(.vcf) 或 CSV");
            }

            var written = await _contacts
                .UpsertManyAsync(entries.Select(entry => entry.ToContact()), ct)
                .ConfigureAwait(false);

            _logger.LogInformation("导入联系人 {File}：解析 {Parsed} 条，写入 {Written} 条", path, entries.Count, written);
            return new ContactExchangeResult(written, $"导入完成：写入 {written} 位联系人");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入联系人失败 {File}", path);
            return new ContactExchangeResult(-1, "导入失败：" + ex.Message);
        }
    }

    public async Task<ContactExchangeResult> ExportAsync(
        string path,
        ContactFileFormat? format = null,
        CancellationToken ct = default)
    {
        try
        {
            var contacts = await _contacts.GetAllAsync(ct: ct).ConfigureAwait(false);
            if (contacts.Count == 0)
            {
                return new ContactExchangeResult(-1, "地址簿为空，暂无可导出的联系人");
            }

            var actualFormat = format ?? ContactFormats.DetectFormat(path);
            var content = ContactFormats.Write(contacts, actualFormat);

            // CSV 带 UTF-8 BOM，Excel 打开中文不乱码；vCard 保持无 BOM。
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: actualFormat == ContactFileFormat.Csv);
            await File.WriteAllTextAsync(path, content, encoding, ct).ConfigureAwait(false);

            return new ContactExchangeResult(contacts.Count, $"已导出 {contacts.Count} 位联系人");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出联系人失败 {File}", path);
            return new ContactExchangeResult(-1, "导出失败：" + ex.Message);
        }
    }
}
