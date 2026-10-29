using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ZungMail.Core.Services;

namespace ZungMail.App.Services;

/// <summary>
/// 把邮件 HTML 的常见标签与内联 CSS 子集转换为 <see cref="FlowDocument"/>，
/// 供 WebView2 不可用时的降级阅读使用。
/// 仅覆盖静态排版子集：不支持 float/flex/grid、绝对定位、外部样式表、背景图片与媒体查询。
/// </summary>
internal static class HtmlFlowDocumentBuilder
{
    /// <summary>降级位面中图片的最大显示宽度（DIP）。</summary>
    private const double MaxImageWidth = 640;

    private static readonly SolidColorBrush BorderBrush = CreateBrush(0xD6, 0xDC, 0xE6);
    private static readonly SolidColorBrush QuoteBrush = CreateBrush(0xC8, 0xD2, 0xE0);
    private static readonly SolidColorBrush LinkBrush = CreateBrush(0x1F, 0x6F, 0xD1);

    /// <summary>解析并构建文档；HTML 为空或解析失败时返回 false，调用方回退到纯文本。</summary>
    public static bool TryBuild(string? html, bool isDark, double baseFontSize, out FlowDocument document)
    {
        // 统一字族：整篇文档一个字体栈，邮件自带的 font-family / <font face> 一律忽略（字号颜色仍保留）
        document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = BodyFontFamily };
        if (string.IsNullOrWhiteSpace(html)) return false;

        try
        {
            var parsed = new HtmlParser().ParseDocument(html);
            var root = (INode?)parsed.Body ?? parsed.DocumentElement;
            if (root is null) return false;

            var sheet = CssSheet.Parse(parsed, isDark);
            var renderer = new Renderer(sheet, isDark);
            renderer.RenderChildren(root.ChildNodes, document.Blocks, CssStyle.Create(baseFontSize));

            return document.Blocks.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---------------- 渲染 ----------------

    private sealed class Renderer
    {
        private readonly CssSheet _sheet;
        private readonly bool _isDark;
        private readonly Dictionary<string, BitmapImage?> _imageCache = new(StringComparer.OrdinalIgnoreCase);

        public Renderer(CssSheet sheet, bool isDark)
        {
            _sheet = sheet;
            _isDark = isDark;
        }

        /// <summary>渲染一组节点：行内内容攒成段落，遇到块级元素先落盘再继续。</summary>
        public void RenderChildren(INodeList nodes, BlockCollection target, CssStyle parent)
        {
            var buffer = new List<Inline>();

            foreach (var node in nodes)
            {
                if (node is IText text)
                {
                    AppendText(text.Data, buffer, parent);
                    continue;
                }

                if (node is not IElement element) continue;

                var tag = element.LocalName.ToLowerInvariant();
                if (tag is "style" or "script" or "head" or "title" or "meta" or "link") continue;

                var style = _sheet.Apply(element, parent);
                if (style.Hidden) continue;

                if (IsBlock(tag))
                {
                    FlushParagraph(buffer, target);
                    RenderBlock(element, tag, target, style);
                }
                else if (tag == "img")
                {
                    AppendImage(element, buffer, style);
                }
                else
                {
                    AppendInline(element, tag, buffer, style);
                }
            }

            FlushParagraph(buffer, target);
        }

        private static void FlushParagraph(List<Inline> buffer, BlockCollection target)
        {
            if (buffer.Count == 0) return;

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var inline in buffer) paragraph.Inlines.Add(inline);
            buffer.Clear();

            TrimEdges(paragraph);
            if (paragraph.Inlines.Count == 0) return;
            target.Add(paragraph);
        }

        /// <summary>去掉段落首尾用于排版的空白，避免出现空行。</summary>
        private static void TrimEdges(Paragraph paragraph)
        {
            while (paragraph.Inlines.FirstInline is Run first && string.IsNullOrWhiteSpace(first.Text))
                paragraph.Inlines.Remove(first);

            while (paragraph.Inlines.LastInline is Run last)
            {
                var trimmed = last.Text.TrimEnd();
                if (trimmed.Length == last.Text.Length && trimmed.Length > 0) break;
                if (trimmed.Length == 0) paragraph.Inlines.Remove(last);
                else last.Text = trimmed;
                break;
            }
        }

        private void RenderBlock(IElement element, string tag, BlockCollection target, CssStyle style)
        {
            switch (tag)
            {
                case "p":
                case "address":
                case "figcaption":
                    RenderParagraph(element, target, style);
                    return;

                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    style.FontWeight = FontWeights.Bold;
                    style.FontSize *= tag switch
                    {
                        "h1" => 1.8,
                        "h2" => 1.5,
                        "h3" => 1.3,
                        "h4" => 1.15,
                        "h5" => 1.05,
                        _ => 1.0
                    };
                    style.Margin ??= new Thickness(0, 10, 0, 6);
                    RenderParagraph(element, target, style);
                    return;

                case "hr":
                    target.Add(new Paragraph
                    {
                        Margin = new Thickness(0, 10, 0, 10),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        BorderBrush = BorderBrush
                    });
                    return;

                case "ul":
                case "ol":
                    RenderList(element, tag, target, style);
                    return;

                case "table":
                    RenderTable(element, target, style);
                    return;

                case "blockquote":
                    RenderQuote(element, target, style);
                    return;

                case "pre":
                    RenderPre(element, target, style);
                    return;

                case "li":
                case "dd":
                case "dt":
                    RenderParagraph(element, target, style);
                    return;

                default:
                    if (HasBlockChild(element)) RenderContainer(element, target, style);
                    else RenderParagraph(element, target, style);
                    return;
            }
        }

        private void RenderParagraph(IElement element, BlockCollection target, CssStyle style)
        {
            var paragraph = new Paragraph
            {
                Margin = style.Margin ?? new Thickness(0, 0, 0, 6),
                Padding = style.Padding ?? default,
                Background = style.Background,
                TextAlignment = style.Alignment ?? TextAlignment.Left
            };
            if (style.LineHeight.HasValue) paragraph.LineHeight = style.LineHeight.Value;

            AppendInlineChildren(element.ChildNodes, paragraph.Inlines, style);
            TrimEdges(paragraph);
            target.Add(paragraph);
        }

        /// <summary>块级容器（div/section/td 等）：保留背景与间距，内部继续按块渲染。</summary>
        private void RenderContainer(IElement element, BlockCollection target, CssStyle style)
        {
            var section = new Section
            {
                Margin = style.Margin ?? default,
                Padding = style.Padding ?? default,
                Background = style.Background,
                TextAlignment = style.Alignment ?? TextAlignment.Left
            };

            RenderChildren(element.ChildNodes, section.Blocks, style);
            if (section.Blocks.Count == 0) return;
            target.Add(section);
        }

        private void RenderQuote(IElement element, BlockCollection target, CssStyle style)
        {
            var section = new Section
            {
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(10, 2, 0, 2),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = QuoteBrush
            };

            RenderChildren(element.ChildNodes, section.Blocks, style);
            target.Add(section);
        }

        private void RenderPre(IElement element, BlockCollection target, CssStyle style)
        {
            var paragraph = new Paragraph
            {
                Margin = style.Margin ?? new Thickness(0, 0, 0, 8),
                Padding = style.Padding ?? new Thickness(8),
                Background = style.Background,
                FontFamily = MonospaceFamily
            };

            var lines = element.TextContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.Add(new Run(lines[i].Replace("\t", "    ")));
            }

            target.Add(paragraph);
        }

        private void RenderList(IElement element, string tag, BlockCollection target, CssStyle style)
        {
            var list = new List
            {
                MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = style.Margin ?? new Thickness(0, 0, 0, 6),
                Padding = new Thickness(20, 0, 0, 0),
                Background = style.Background
            };

            foreach (var item in element.Children.Where(c => c.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
            {
                var listItem = new ListItem();
                var itemStyle = _sheet.Apply(item, style);
                RenderChildren(item.ChildNodes, listItem.Blocks, itemStyle);
                if (listItem.Blocks.Count == 0) listItem.Blocks.Add(new Paragraph());
                list.ListItems.Add(listItem);
            }

            if (list.ListItems.Count > 0) target.Add(list);
        }

        private void RenderTable(IElement element, BlockCollection target, CssStyle style)
        {
            var rows = CollectRows(element);
            if (rows.Count == 0) return;

            var table = new Table
            {
                CellSpacing = 0,
                Margin = style.Margin ?? new Thickness(0, 6, 0, 10),
                Background = style.Background
            };

            var columns = rows.Max(r => r.Cells.Sum(c => Math.Max(1, ParseInt(c.GetAttribute("colspan")) ?? 1)));
            for (var i = 0; i < Math.Max(1, columns); i++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            // 边框既可能写在 table 上（border 属性/样式），也可能只写在 td/th 的 style 里；
            // 逐单元格判定，避免“只有部分单元格声明了边框”时整表丢线。
            var tableBordered = style.TableBorder || ParseInt(element.GetAttribute("border")) > 0;
            foreach (var (rowElement, cells) in rows)
            {
                var row = new TableRow();
                var rowStyle = rowElement is null ? style : _sheet.Apply(rowElement, style);
                if (rowStyle.Background is not null) row.Background = rowStyle.Background;

                foreach (var cellElement in cells)
                {
                    var cellStyle = _sheet.Apply(cellElement, style);
                    var cellBordered = tableBordered || cellStyle.TableBorder;
                    var cell = new TableCell
                    {
                        ColumnSpan = Math.Max(1, ParseInt(cellElement.GetAttribute("colspan")) ?? 1),
                        RowSpan = Math.Max(1, ParseInt(cellElement.GetAttribute("rowspan")) ?? 1),
                        Padding = cellStyle.Padding ?? new Thickness(4, 2, 4, 2),
                        Background = cellStyle.Background,
                        BorderBrush = cellBordered ? BorderBrush : null,
                        BorderThickness = cellBordered ? new Thickness(1) : new Thickness(0),
                        TextAlignment = cellStyle.Alignment ?? TextAlignment.Left
                    };

                    RenderChildren(cellElement.ChildNodes, cell.Blocks, cellStyle);
                    if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                    row.Cells.Add(cell);
                }

                group.Rows.Add(row);
            }

            target.Add(table);
        }

        private static List<(IElement? Row, List<IElement> Cells)> CollectRows(IElement table)
        {
            var result = new List<(IElement?, List<IElement>)>();

            foreach (var child in table.Children)
            {
                var tag = child.LocalName.ToLowerInvariant();
                if (tag == "tr")
                {
                    result.Add((child, CollectCells(child)));
                }
                else if (tag is "thead" or "tbody" or "tfoot")
                {
                    foreach (var row in child.Children.Where(c => c.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add((row, CollectCells(row)));
                    }
                }
            }

            return result;

            static List<IElement> CollectCells(IElement row)
                => row.Children
                    .Where(c => c.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                                c.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }

        private void AppendInlineChildren(INodeList nodes, InlineCollection target, CssStyle parent)
        {
            foreach (var node in nodes)
            {
                if (node is IText text)
                {
                    AppendText(text.Data, target, parent);
                    continue;
                }

                if (node is not IElement element) continue;

                var tag = element.LocalName.ToLowerInvariant();
                if (tag is "script" or "style" or "head" or "title" or "meta" or "link") continue;

                var style = _sheet.Apply(element, parent);
                if (style.Hidden) continue;

                switch (tag)
                {
                    case "br":
                        target.Add(new LineBreak());
                        break;

                    case "img":
                        AppendImage(element, target, style);
                        break;

                    default:
                        AppendInline(element, tag, target, style);
                        break;
                }
            }
        }

        private void AppendText(string? data, ICollection<Inline> target, CssStyle style)
        {
            if (string.IsNullOrEmpty(data)) return;

            var text = style.Pre ? data : Regex.Replace(data, @"\s+", " ");
            if (text.Length == 0) return;

            var run = new Run(text);
            ApplyInlineStyle(run, style);
            target.Add(run);
        }

        private void AppendInline(IElement element, string tag, ICollection<Inline> target, CssStyle style)
        {
            var span = new Span();
            switch (tag)
            {
                case "b":
                case "strong":
                    style.FontWeight = FontWeights.Bold;
                    break;
                case "i":
                case "em":
                case "cite":
                    style.FontStyle = FontStyles.Italic;
                    break;
                case "u":
                case "ins":
                    style.Underline = true;
                    break;
                case "s":
                case "strike":
                case "del":
                    style.Strike = true;
                    break;
                case "code":
                case "kbd":
                case "samp":
                    style.FontFamily = MonospaceFamily;
                    break;
                case "a":
                    style.Underline = true;
                    if (!_isDark) style.Foreground = LinkBrush;
                    break;
                case "font":
                    ApplyFontAttributes(element, style);
                    break;
            }

            ApplyInlineStyle(span, style);
            AppendInlineChildren(element.ChildNodes, span.Inlines, style);
            if (span.Inlines.Count > 0) target.Add(span);
        }

        private void ApplyFontAttributes(IElement element, CssStyle style)
        {
            var color = element.GetAttribute("color");
            if (!_isDark && !string.IsNullOrWhiteSpace(color) && TryParseBrush(color) is { } brush)
                style.Foreground = brush;

            // <font face> 不再生效：正文统一字体（颜色与 size 仍按原邮件保留）

            var size = ParseInt(element.GetAttribute("size"));
            if (size is >= 1 and <= 7)
                style.FontSize = size switch
                {
                    1 => 10,
                    2 => 13,
                    3 => 16,
                    4 => 18,
                    5 => 24,
                    6 => 32,
                    _ => 48
                };
        }

        private static void ApplyInlineStyle(Inline target, CssStyle style)
        {
            target.FontSize = style.FontSize;
            target.FontWeight = style.FontWeight;
            target.FontStyle = style.FontStyle;
            if (style.FontFamily is not null) target.FontFamily = style.FontFamily;
            if (style.Foreground is not null) target.Foreground = style.Foreground;
            if (style.Background is not null) target.Background = style.Background;

            if (style.Underline || style.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (style.Underline)
                {
                    foreach (var decoration in TextDecorations.Underline) decorations.Add(decoration);
                }

                if (style.Strike)
                {
                    foreach (var decoration in TextDecorations.Strikethrough) decorations.Add(decoration);
                }

                decorations.Freeze();
                target.TextDecorations = decorations;
            }
        }

        private void AppendImage(IElement element, ICollection<Inline> target, CssStyle style)
        {
            var image = CreateImage(element, style);
            if (image is null) return;

            target.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom });
        }

        private Image? CreateImage(IElement element, CssStyle style)
        {
            var path = ResolveLocalPath(element.GetAttribute("src"));
            if (path is null) return null;

            var bitmap = LoadBitmap(path);
            if (bitmap is null) return null;

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = MaxImageWidth
            };

            var width = ParseLength(element.GetAttribute("width")) ?? style.Width;
            if (width is > 0) image.Width = Math.Min(width.Value, MaxImageWidth);

            var height = ParseLength(element.GetAttribute("height")) ?? style.Height;
            if (height is > 0) image.Height = height.Value;

            return image;
        }

        private BitmapImage? LoadBitmap(string path)
        {
            if (_imageCache.TryGetValue(path, out var cached)) return cached;

            BitmapImage? bitmap = null;
            try
            {
                if (File.Exists(path))
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
            }
            catch (Exception)
            {
                bitmap = null;
            }

            _imageCache[path] = bitmap;
            return bitmap;
        }
    }

    // ---------------- CSS 子集 ----------------

    private sealed class CssStyle
    {
        public double FontSize { get; set; }
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public FontStyle FontStyle { get; set; } = FontStyles.Normal;
        public FontFamily? FontFamily { get; set; }
        public Brush? Foreground { get; set; }
        public Brush? Background { get; set; }
        public TextAlignment? Alignment { get; set; }
        public Thickness? Margin { get; set; }
        public Thickness? Padding { get; set; }
        public double? LineHeight { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Underline { get; set; }
        public bool Strike { get; set; }
        public bool Pre { get; set; }
        public bool Hidden { get; set; }
        public bool TableBorder { get; set; }

        public static CssStyle Create(double fontSize) => new() { FontSize = fontSize };

        public CssStyle Clone() => (CssStyle)MemberwiseClone();
    }

    private sealed class CssSheet
    {
        private readonly List<CssRule> _rules = new();
        private readonly bool _isDark;

        private CssSheet(bool isDark) => _isDark = isDark;

        public static CssSheet Parse(IHtmlDocument document, bool isDark)
        {
            var sheet = new CssSheet(isDark);

            foreach (var styleElement in document.QuerySelectorAll("style"))
            {
                var css = Regex.Replace(styleElement.TextContent, @"/\*[\s\S]*?\*/", " ");
                foreach (Match match in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
                {
                    var declarations = ParseDeclarations(match.Groups[2].Value);
                    if (declarations.Count == 0) continue;

                    foreach (var selector in match.Groups[1].Value.Split(','))
                    {
                        if (TryParseSelector(selector.Trim(), out var rule))
                        {
                            rule.Declarations = declarations;
                            sheet._rules.Add(rule);
                        }
                    }
                }
            }

            return sheet;
        }

        /// <summary>按 CSS 层叠顺序合并规则与内联样式；字体、颜色、对齐继承，盒模型属性不继承。</summary>
        public CssStyle Apply(IElement element, CssStyle parent)
        {
            var style = parent.Clone();
            style.Background = null;
            style.Margin = null;
            style.Padding = null;
            style.LineHeight = null;
            style.Width = null;
            style.Height = null;
            style.Underline = false;
            style.Strike = false;
            style.Hidden = false;
            style.TableBorder = false;

            foreach (var rule in _rules.Where(r => r.Matches(element)).OrderBy(r => r.Specificity))
                ApplyDeclarations(rule.Declarations, style, parent);

            ApplyDeclarations(ParseDeclarations(element.GetAttribute("style")), style, parent);
            ApplyAttributes(element, style);

            return style;
        }

        private void ApplyDeclarations(Dictionary<string, string> declarations, CssStyle style, CssStyle parent)
        {
            foreach (var (name, value) in declarations)
            {
                switch (name)
                {
                    case "font-size":
                        if (ParseLength(value, parent.FontSize) is { } fontSize && fontSize > 0) style.FontSize = fontSize;
                        break;

                    case "font-weight":
                        if (value.Contains("bold", StringComparison.OrdinalIgnoreCase) ||
                            (int.TryParse(value, out var weight) && weight >= 600))
                            style.FontWeight = FontWeights.Bold;
                        else if (value.Contains("normal", StringComparison.OrdinalIgnoreCase))
                            style.FontWeight = FontWeights.Normal;
                        break;

                    case "font-style":
                        if (value.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
                            value.Contains("oblique", StringComparison.OrdinalIgnoreCase))
                            style.FontStyle = FontStyles.Italic;
                        break;

                    case "font-family":
                        // 忽略邮件自带字族：正文统一字体（字号颜色不受影响）
                        break;

                    case "color":
                        if (!_isDark && TryParseBrush(value) is { } foreground) style.Foreground = foreground;
                        break;

                    case "background":
                    case "background-color":
                        if (!_isDark && !value.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                            TryParseBrush(value) is { } background)
                            style.Background = background;
                        break;

                    case "text-align":
                        style.Alignment = value.ToLowerInvariant() switch
                        {
                            "center" => TextAlignment.Center,
                            "right" => TextAlignment.Right,
                            "justify" => TextAlignment.Justify,
                            "left" => TextAlignment.Left,
                            _ => style.Alignment
                        };
                        break;

                    case "text-decoration":
                    case "text-decoration-line":
                        if (value.Contains("underline", StringComparison.OrdinalIgnoreCase)) style.Underline = true;
                        if (value.Contains("line-through", StringComparison.OrdinalIgnoreCase)) style.Strike = true;
                        break;

                    case "line-height":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
                            style.LineHeight = multiplier * style.FontSize;
                        else if (ParseLength(value, style.FontSize) is { } height)
                            style.LineHeight = height;
                        break;

                    case "margin":
                        style.Margin = ParseThickness(value) ?? style.Margin;
                        break;

                    case "margin-top":
                    case "margin-right":
                    case "margin-bottom":
                    case "margin-left":
                        style.Margin = SetSide(style.Margin, name, value);
                        break;

                    case "padding":
                        style.Padding = ParseThickness(value) ?? style.Padding;
                        break;

                    case "padding-top":
                    case "padding-right":
                    case "padding-bottom":
                    case "padding-left":
                        style.Padding = SetSide(style.Padding, name.Replace("padding", "margin"), value);
                        break;

                    case "white-space":
                        style.Pre = value.Contains("pre", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "display":
                        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) style.Hidden = true;
                        break;

                    case "width":
                        style.Width = ParseLength(value, style.FontSize);
                        break;

                    case "height":
                        style.Height = ParseLength(value, style.FontSize);
                        break;

                    case "border":
                    case "border-width":
                    case "border-top":
                    case "border-bottom":
                    case "border-left":
                    case "border-right":
                        if (!value.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                            !value.Contains("none", StringComparison.OrdinalIgnoreCase))
                            style.TableBorder = true;
                        break;
                }
            }
        }

        /// <summary>标签自带的表现型属性（align/bgcolor/color/face/size）。</summary>
        private void ApplyAttributes(IElement element, CssStyle style)
        {
            var align = element.GetAttribute("align");
            if (style.Alignment is null && !string.IsNullOrWhiteSpace(align))
            {
                style.Alignment = align.ToLowerInvariant() switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    "justify" => TextAlignment.Justify,
                    _ => TextAlignment.Left
                };
            }

            if (_isDark) return;

            var background = element.GetAttribute("bgcolor");
            if (style.Background is null && !string.IsNullOrWhiteSpace(background) &&
                TryParseBrush(background) is { } brush)
                style.Background = brush;

            var color = element.GetAttribute("color");
            if (style.Foreground is null && !string.IsNullOrWhiteSpace(color) &&
                TryParseBrush(color) is { } foreground)
                style.Foreground = foreground;
        }

        private static Thickness SetSide(Thickness? current, string name, string value)
        {
            var thickness = current ?? new Thickness(0);
            var length = ParseLength(value);
            if (length is null) return thickness;

            return name switch
            {
                "margin-top" or "padding-top" => new Thickness(thickness.Left, length.Value, thickness.Right, thickness.Bottom),
                "margin-right" or "padding-right" => new Thickness(thickness.Left, thickness.Top, length.Value, thickness.Bottom),
                "margin-bottom" or "padding-bottom" => new Thickness(thickness.Left, thickness.Top, thickness.Right, length.Value),
                _ => new Thickness(length.Value, thickness.Top, thickness.Right, thickness.Bottom)
            };
        }
    }

    private sealed class CssRule
    {
        public string? Tag { get; init; }
        public string? Id { get; init; }
        public string[] Classes { get; init; } = Array.Empty<string>();
        public int Specificity { get; init; }
        public Dictionary<string, string> Declarations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Matches(IElement element)
        {
            if (Tag is not null && !element.LocalName.Equals(Tag, StringComparison.OrdinalIgnoreCase)) return false;
            if (Id is not null && !string.Equals(element.Id, Id, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (var name in Classes)
            {
                if (!element.ClassList.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
            }

            return true;
        }
    }

    // ---------------- 解析工具 ----------------

    /// <summary>正文统一字体（与 WebView2 路径同一份声明，见 MailBodyTypography）。</summary>
    internal static readonly FontFamily BodyFontFamily = new(MailBodyTypography.WpfFontFamily);

    private static readonly FontFamily MonospaceFamily = new(MailBodyTypography.WpfMonospaceFamily);

    private static bool TryParseSelector(string selector, out CssRule rule)
    {
        rule = new CssRule();

        // 只支持「标签 / .类 / #ID」的简单组合，含组合符、伪类、属性的规则直接忽略
        if (selector.Length == 0 ||
            selector.IndexOfAny(new[] { ' ', '>', '+', '~', ':', '[', ']', '(', ')', '*', '@' }) >= 0)
            return false;

        var tagMatch = Regex.Match(selector, @"^[a-zA-Z][\w-]*");
        string? tag = tagMatch.Success ? tagMatch.Value : null;
        var rest = tagMatch.Success ? selector[tagMatch.Length..] : selector;

        var ids = new List<string>();
        var classes = new List<string>();
        foreach (Match token in Regex.Matches(rest, @"([.#])([\w-]+)"))
        {
            if (token.Groups[1].Value == "#") ids.Add(token.Groups[2].Value);
            else classes.Add(token.Groups[2].Value);
        }

        // 无法完全消费的选择器（如 td.foo.bar 之外的怪异写法）视为不支持
        var consumed = (tagMatch.Success ? tagMatch.Length : 0) +
                       ids.Sum(i => i.Length + 1) + classes.Sum(c => c.Length + 1);
        if (consumed != selector.Length) return false;

        rule = new CssRule
        {
            Tag = tag,
            Id = ids.FirstOrDefault(),
            Classes = classes.ToArray(),
            Specificity = ids.Count * 100 + classes.Count * 10 + (tag is null ? 0 : 1)
        };
        return true;
    }

    private static Dictionary<string, string> ParseDeclarations(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var part in text.Split(';'))
        {
            var index = part.IndexOf(':');
            if (index <= 0) continue;

            var name = part[..index].Trim().ToLowerInvariant();
            var value = part[(index + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;

            result[name] = value;
        }

        return result;
    }

    private static Thickness? ParseThickness(string value)
    {
        var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var lengths = new List<double>();
        foreach (var part in parts)
        {
            var length = ParseLength(part);
            if (length is null) return null;
            lengths.Add(length.Value);
            if (lengths.Count == 4) break;
        }

        return lengths.Count switch
        {
            1 => new Thickness(lengths[0]),
            2 => new Thickness(lengths[1], lengths[0], lengths[1], lengths[0]),
            3 => new Thickness(lengths[1], lengths[0], lengths[1], lengths[2]),
            4 => new Thickness(lengths[3], lengths[0], lengths[2], lengths[1]),
            _ => null
        };
    }

    private static double? ParseLength(string? value, double? referenceFontSize = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim().ToLowerInvariant();
        if (value is "auto" or "normal" or "inherit" or "initial" or "unset") return null;
        if (value.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)) return null;

        double Parse(string number)
            => double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;

        if (value.EndsWith("px", StringComparison.Ordinal))
        {
            var number = Parse(value[..^2]);
            return double.IsNaN(number) ? null : number;
        }

        if (value.EndsWith("pt", StringComparison.Ordinal))
        {
            var number = Parse(value[..^2]);
            return double.IsNaN(number) ? null : number * 96d / 72d;
        }

        if (value.EndsWith("em", StringComparison.Ordinal))
        {
            var number = Parse(value[..^2]);
            return double.IsNaN(number) ? null : number * (referenceFontSize ?? 13.5);
        }

        if (value.EndsWith("rem", StringComparison.Ordinal))
        {
            var number = Parse(value[..^3]);
            return double.IsNaN(number) ? null : number * 16d;
        }

        if (value.EndsWith("%", StringComparison.Ordinal))
        {
            var number = Parse(value[..^1]);
            return double.IsNaN(number) ? null : number / 100d * (referenceFontSize ?? 13.5);
        }

        var unitless = Parse(value);
        return double.IsNaN(unitless) ? null : unitless;
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static Brush? TryParseBrush(string value)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveLocalPath(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        source = source.Trim();
        if (source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return null;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return uri.LocalPath;
            if (uri.Scheme is "http" or "https" or "data" or "mailto") return null;
        }

        return Path.IsPathRooted(source) && File.Exists(source) ? source : null;
    }

    private static bool IsBlock(string tag) => tag switch
    {
        "html" or "body" or "div" or "p" or "section" or "article" or "header" or "footer" or
        "main" or "aside" or "center" or "address" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
        "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "blockquote" or "pre" or "table" or
        "thead" or "tbody" or "tfoot" or "tr" or "td" or "th" or "hr" or "form" or "fieldset" or
        "figure" or "figcaption" => true,
        _ => false
    };

    private static bool HasBlockChild(IElement element)
        => element.Children.Any(c => IsBlock(c.LocalName.ToLowerInvariant()));
}
