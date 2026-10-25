using ZungMail.Core.Services;

namespace ZungMail.App.Services;

/// <summary>
/// 正文 WebView2 里注入的「图片双击」脚本。
///
/// 正文按不可信内容处理，<c>IsScriptEnabled</c> 保持关闭：邮件自带的 &lt;script&gt; 与 on* 事件
/// 属性执行不了（消毒已剥离 on* 属性，CSP 另有 <c>script-src 'none'</c>）。
/// 这里用宿主侧 <c>ExecuteScriptAsync</c> 注入，等同于 DevTools 求值，不受页面 CSP 限制；
/// 脚本只做一件事——把被双击的图片地址回传给宿主（<c>IsWebMessageEnabled</c> 单独打开）。
/// </summary>
public static class MailBodyImageScript
{
    /// <summary>注入标记：重复注入时用它短路，避免每次导航叠加多个监听器。</summary>
    public const string Marker = "__zungmailImageDbl";

    /// <summary>实际注入的脚本（消息类型从 <see cref="MailBodyImageMessage.TypeName"/> 取，避免两边写死对不上）。</summary>
    public static string Inject { get; } = Template.Replace("__message_type__", MailBodyImageMessage.TypeName);

    private const string Template = """
        (function () {
          if (window.__zungmailImageDbl) { return; }
          window.__zungmailImageDbl = 1;

          // 双击目标可能是 img 内部的包装元素，向上找到最近的 IMG
          var findImage = function (node) {
            while (node && node.nodeType === 1) {
              if (node.tagName === 'IMG') { return node; }
              node = node.parentNode;
            }
            return null;
          };

          document.addEventListener('dblclick', function (event) {
            var image = findImage(event.target);
            if (!image) { return; }

            var source = image.currentSrc || image.src || '';
            if (!source) { return; }

            event.preventDefault();
            event.stopPropagation();

            window.chrome.webview.postMessage({
              type: '__message_type__',
              src: source,
              alt: image.alt || '',
              width: image.naturalWidth || 0,
              height: image.naturalHeight || 0
            });
          }, true);

          // 可点开的提示：鼠标移到正文图片上变放大镜
          var style = document.createElement('style');
          style.textContent = 'img { cursor: zoom-in; }';
          (document.head || document.documentElement).appendChild(style);
        })();
        """;
}
