<div align="center">
  <img src="docs/brand/zungmail-logo/png/zungmail-128.png" width="96" alt="ZungMail" />
  <h1>ZungMail</h1>
  <p><strong>面向货代业务的 Windows 邮件工作台</strong><br />
  把「一票货」相关的邮件、单号、时效节点和催办，收在一个界面里。</p>
  <p>
    <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-0b6cf0" />
    <img alt=".net" src="https://img.shields.io/badge/.NET-8.0-512bd4" />
    <img alt="ui" src="https://img.shields.io/badge/UI-WPF%20%2B%20WebView2-6b4fbb" />
    <img alt="license" src="https://img.shields.io/badge/license-MIT-2ea44f" />
    <img alt="version" src="https://img.shields.io/badge/version-1.2.0-6b7280" />
  </p>
</div>

---

ZungMail 是一个本地优先（local-first）的桌面邮件客户端：邮件、索引、标签、规则、草稿全部保存在本机 SQLite 里，只有收发信时才连邮件服务器。在通用邮件客户端能力之上，它把货代日常最费神的三件事做进了主界面：

- **找得到**：自动抽取 Job / SO / B/L / 箱号 / 封号 / 发票号 / 空运单，按「一票货」跨文件夹、跨账户聚合。
- **不误期**：截关、截补料、VGM、ETD/ETA 抽取成倒计时看板，到期提醒一次。
- **不漏催**：登记「等待回复」，到点检查是否收到回复，没回就提醒。

## 功能一览

**邮件基础能力**

| 能力 | 说明 |
|---|---|
| IMAP / SMTP | 强制 SSL（IMAP 993 / SMTP 465），支持多账户、主账户优先同步 |
| 即时收信 | 收件箱 IDLE 推送（秒级），服务器不支持时自动退回定时增量同步 |
| 本地优先写操作 | 已读 / 星标 / 移动 / 删除 / 打标签先落本地再回写；断网进待同步队列，恢复后重试 |
| 分页与按需下载 | 列表分页（默认 100 封/页），正文与附件按需下载，只同步邮件头也能用 |
| 全文检索 | SQLite FTS5，支持 `from:` / `subject:` / `has:attachment` / `after:7d` 等结构化语法，`@` 唤起语法菜单 |
| 写信与模板 | 富文本编辑（WebView2）、按账户签名、整封模板与快速文本、定时发送、草稿自动恢复 |
| 安全 | 账户口令 DPAPI 加密；默认拦截远程图片（防追踪像素）；可启用客户端锁屏与全局热键 |

**货代工作流**

- **一票货案件**：同一单号的邮件聚成时间轴，可导出资料包交接。
- **业务单号**：Job / SO / B/L / 箱号（含校验位）/ 封号 / 发票号 / PO / 空运单，优先级从高到低归集。
- **行业邮件类型**：订舱确认、SI、VGM、提单草稿、AN、账单、查验、甩柜、延误等，异常类型红色角标。
- **时效节点**：截关、截补料、VGM、ETD、ETA 看板与倒计时。
- **等待回复**：按收件人登记跟进，到点提醒，对方回复后自动结束。
- **可疑邮件提示**：陌生发件人角标 + 仿冒自有域名的钓鱼警示（本地规则，不查外部情报）。

## 技术栈

- **客户端**：WPF（.NET 8）、WPF-UI、CommunityToolkit.Mvvm、WebView2（正文与编辑器渲染）
- **协议层**：MailKit / MimeKit（IMAP、SMTP、MIME 解析）
- **存储**：SQLite（WAL + FTS5），手写迁移与 `SchemaVersion`
- **升级**：客户端 + 独立升级服务（Minimal API），SHA256 校验、MSI / 安装包 / 便携版三种形态
- **打包**：WiX v4（MSI）+ NSIS（安装包 / 便携 zip），支持可选代码签名

## 目录结构

```
src/
  ZungMail.Core            领域模型与抽象（仓储接口、策略纯函数）
  ZungMail.Application     应用服务（同步协调、规则、标签、跟进、清理）
  ZungMail.Infrastructure  SQLite 仓储、配置、日志、升级、加解密
  ZungMail.Mail            IMAP/SMTP 协议实现（MailKit）
  ZungMail.App             WPF 客户端
  ZungMail.Update.Protocol 升级协议契约
  ZungMail.UpdateServer    升级服务端
tests/
  ZungMail.Tests           Core / Infrastructure / Application / Mail 的单测与集成测试
  ZungMail.UiTests         XAML 结构断言与视图模型旅程测试（STA）
  ZungMail.UpdateServer.Tests 升级服务端测试
docs/                      设计文档（见下方「文档」）
packaging/                 MSI / NSIS 打包脚本与说明
```

## 开始使用

**环境要求**：Windows 10 1607+ / Windows 11（x64）、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（`global.json` 固定 8.0.404，允许向前滚）；运行还需要 WebView2 Runtime（Win11 自带，Win10 多数已随 Edge 安装）。

```powershell
# 构建
dotnet build ZungMail.sln -c Release

# 运行（输出目录在仓库根的 bin/ 下）
.\bin\ZungMail.App\Release\net8.0-windows\ZungMail.exe

# 测试（三套都要绿）
dotnet test tests\ZungMail.Tests\ZungMail.Tests.csproj -c Release
dotnet test tests\ZungMail.UiTests\ZungMail.UiTests.csproj -c Release
dotnet test tests\ZungMail.UpdateServer.Tests\ZungMail.UpdateServer.Tests.csproj -c Release
```

首次启动后到 **设置 → 账户** 添加邮箱（QQ / 163 / 企业邮箱通常填「授权码」而不是登录密码），测试连接通过后保存即可开始同步。

## 打包与发布

```powershell
.\packaging\build-installer.ps1          # NSIS 安装包 + 便携版 zip
.\packaging\build-msi.ps1                # x64 MSI
.\packaging\build-msi.ps1 -SkipPublish   # 复用发布目录，重算 SHA256SUMS
```

详见 [packaging/README.md](packaging/README.md)，其中还包含**代码签名**配置（`ZUNGMAIL_SIGN_*` 环境变量）与安全软件误杀的处理办法。版本号唯一来源是 `Directory.Build.props` 的 `<Version>`。

## 数据与隐私

- 数据目录：`%APPDATA%\ZungMail`（数据库 `mail.db`、原始邮件 `eml\`、日志 `logs\`、头像 `avatars\`）。
- 运行日志按天滚动，**最多保留 7 天**，超期自动清理。
- 除收发信、可选的头像查询（Gravatar，可在设置里关闭）与在线升级检测外，客户端不向外发送任何数据；没有遥测与云端同步。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/货代业务工作流设计.md](docs/货代业务工作流设计.md) | 一票货、单号优先级、邮件类型、时效节点、跟进的判定规则 |
| [docs/架构与开发约定.md](docs/架构与开发约定.md) | 分层与依赖方向、写操作模型、存储迁移、性能与测试约定、已知技术债 |
| [docs/升级服务部署与客户端升级说明.md](docs/升级服务部署与客户端升级说明.md) | 升级服务部署与客户端配置 |
| [docs/产品改名与数据迁移说明.md](docs/产品改名与数据迁移说明.md) | 数据目录接管与兼容清单 |
| [openspec/specs](openspec/specs) | 现行能力规格（可验收的「当前事实」） |
| 内置教程 | 客户端内「设置 → 关于 → 打开使用教程」（离线 HTML，随程序发布） |

## 参与贡献

- 提 Issue 前请先搜一下是否已有同类问题；缺陷请附复现步骤、客户端版本（设置 → 关于）与日志片段（`%APPDATA%\ZungMail\logs\`，注意先删掉与隐私相关的内容）。
- 提交代码请保证：Release 构建 0 警告、三套测试全绿、行为改动用例可验收；界面改动附截图。
- 涉及数据库结构的改动必须新增迁移并补 `DatabaseMigrationTests`。

## 许可证

[MIT](LICENSE) © 2026 Mr.Leung
