# 品牌资源（ZungMail Logo）

本目录保存 **ZungMail 品牌图形的母版** 与设计说明，改动图标时从这里开始，不要直接手改 `src\` 下那份 PNG/ICO。

## 目录结构

```text
docs\brand\
├── README.md                     ← 本文件：母版位置、应用内落点、变更流程
└── zungmail-logo\                ← 设计交付原件（原样保留，不再改写）
    ├── README.md                 ← 设计方的原始说明（设计思路、色板、注意事项）
    ├── zungmail-icon.svg         ← 主图标矢量源（512×512，圆角 118）
    ├── zungmail-mark.svg         ← 纯白单色标记（深色底/水印）
    ├── zungmail-lockup-light.svg ← 横版「图标 + 字标」，浅色底
    ├── zungmail-lockup-dark.svg  ← 横版「图标 + 字标」，深色底
    ├── zungmail.ico              ← 16/24/32/48/64/128/256 七档 32bpp 帧
    ├── png\                      ← 9 档正方形 PNG（16→512）+ 两版组合标 @2x
    └── preview.html              ← 规范预览页（亮/暗底板、尺寸适配、色板）
```

## 应用内生效副本

| 母版 | 应用内文件 | 用途 |
| --- | --- | --- |
| `zungmail-logo\zungmail.ico` | `src\ZungMail.App\Assets\Brand\ZungMail.ico` | 程序图标：任务栏、Alt+Tab、资源管理器、桌面/开始菜单快捷方式、NSIS 安装包、MSI 的 ARP 图标（取自 exe） |
| `zungmail-logo\png\zungmail-256.png` | `src\ZungMail.App\Assets\Brand\ZungMailMark.png` | 设置 → 关于 卡片里的 44px 徽标 |
| `zungmail-logo\png\zungmail-64.png` | `src\ZungMail.App\Assets\Brand\ZungMailMark64.png` | 主窗口标题栏的 22px 徽标 |

引用这些文件的地方（改名/换图标时要一起核对）：

- `src\ZungMail.App\ZungMail.App.csproj`：`<ApplicationIcon>` 与三个 `<Resource>`。
- `src\ZungMail.App\Views\MainWindow.xaml`：标题栏 `<Image Source="pack://application:,,,/Assets/Brand/ZungMailMark64.png">`。
- `src\ZungMail.App\Views\SettingsWindow.xaml`：关于页 `<Image ... ZungMailMark.png>`（同卡片里「个人版」徽标与版本 ToolTip 不变）。
- `src\ZungMail.App\Views\MailReaderWindow.xaml`：独立阅读窗 `Icon=…/ZungMail.ico`。
- `src\ZungMail.App\Assets\help\user-guide.html`：教程侧栏徽标，**内联 SVG**（离线自包含，不引用外部文件）；换图标时要同步这里的 `path`/渐变。
- `packaging\ZungMail.nsi` 的 `ICON` 与 `packaging\build-installer.ps1` 的 `$iconPath`：都指向应用内的 `ZungMail.ico`。

`preview.html`、`mark.svg`、两版 lockup 与其余尺寸 PNG **不参与构建**，只作设计与后续取用，不会进安装包。

## 变更流程

1. 改 `docs\brand\zungmail-logo\zungmail-icon.svg`（或在设计工具里重画后覆盖）。
2. 重新导出：正方形 PNG 16/24/32/48/64/96/128/256/512，以及 7 档 ICO（**32bpp BMP 帧**，WPF `Icon`、`ApplicationIcon` 与旧版 `System.Drawing` 都吃这套；PNG 压缩帧在部分场景会显示空白）。
3. 覆盖上表三份「应用内生效副本」（文件名保持不变，避免改 XAML/csproj）。
4. 同步 `user-guide.html` 里的内联 SVG。
5. 验证：
   ```powershell
   dotnet build ZungMail.sln -c Release            # 0 警告 0 错误
   dotnet test tests\ZungMail.UiTests -c Release   # BrandAssetTests 会校验尺寸档、引用与母版一致
   ```
6. 重打安装包（图标同时进 exe、NSIS 安装器与 MSI 的 ARP 图标）：
   ```powershell
   .\packaging\build-msi.ps1
   .\packaging\build-installer.ps1
   ```

## 设计要点（摘自设计说明）

- 纯首字母 **Z** 徽标：单路径、52px 等宽笔画、`linejoin=bevel` + `linecap=round`，小尺寸不糊。
- 底板渐变 `#6FB0FF → #2F63F7 → #2B18A6`，左上径向高光 + 底部暗部 + 2.5px 内描边，圆角 118/512（接近 Win11 / macOS 比例）。
- 字标渐变白 → 淡蓝并带柔和投影；**薄荷绿 `#22D3BF` 不进图标**，只作界面信号色（未读等）。
- 16 / 24px 档不要加描边或额外装饰，保持单色 Z 轮廓。

| 名称 | 色值 | 用途 |
| --- | --- | --- |
| Brand Blue | `#2F63F7` | 主品牌色、渐变中段 |
| Deep Indigo | `#2B18A6` | 渐变末端 |
| Signal Mint | `#22D3BF` | 界面信号色，不进图标 |
| Ink Navy | `#12235F` | 浅色底字标主色 |

> 母版来自 `F:\Code\WPF\temp_20261022_01\ZungMail-Logo`（2026-10-22 版），本目录内容与之一字不差；后续若设计方再出稿，整包替换 `zungmail-logo\` 并按上面流程同步应用副本。
