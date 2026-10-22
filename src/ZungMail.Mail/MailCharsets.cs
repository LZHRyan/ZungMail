using System.Runtime.CompilerServices;
using System.Text;

namespace ZungMail.Mail;

/// <summary>
/// 旧式字符集注册（GB2312 / GBK、KOI8-R、Windows-1251 等）。
///
/// .NET 只自带 UTF-8 / UTF-16 / Latin-1 等少数编码，其余代码页需显式注册提供程序。
/// 缺少注册时 MimeKit 解析 <c>=?gb2312?B?...?=</c>、<c>charset=koi8-r</c> 这类声明取不到编码，
/// 会退回 Latin-1——中文变成 <c>»Ø¸´</c>、俄语变成 <c>Ñ÷´r</c> 之类的乱码。
///
/// 必须在任何一次 MailKit / MimeKit 解析之前调用，见 <see cref="DependencyInjection.ServiceCollectionExtensions.AddZungMailMail"/>
/// 与 <see cref="ImapSyncService"/> 构造函数。
/// </summary>
public static class MailCharsets
{
    private static int _registered;

    /// <summary>
    /// 程序集加载即注册：MimeKit 会在首次解析时缓存字符集查找结果，
    /// 若此前的任何调用先触发了缓存，后续再注册代码页也无法还原旧式字符集。
    /// 这里是有意为之的库级初始化，故屏蔽「仅限应用程序代码」的提示。
    /// </summary>
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterOnAssemblyLoad() => EnsureRegistered();
#pragma warning restore CA2255

    /// <summary>注册旧式字符集（幂等，可重复调用）。</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
