# ZungMail 打包（安装包 / 便携版）

一键把当前源码打成可分发的 Windows 安装包，构建脚本与 NSIS 脚本都在这个目录。

## 快速开始

```powershell
# 默认：win-x64 自包含（目标机器无需装 .NET），生成安装包 + 便携 zip + 校验值
.\packaging\build-installer.ps1

# 生成 x64 MSI（版本号读取 Directory.Build.props，当前 1.3.6）
.\packaging\build-msi.ps1
```

产物在仓库根目录的 `dist\`：

| 文件 | 说明 |
| --- | --- |
| `ZungMail-<版本>-win-x64-selfcontained-setup.exe` | NSIS 安装包（自包含，约 50 MB） |
| `ZungMail-<版本>-win-x64-selfcontained-portable.zip` | 免安装便携版（解压即用） |
| `ZungMail-<版本>-x64.msi` | Windows Installer 安装包（x64，可用组策略 / Intune / `msiexec /qn` 静默安装） |
| `SHA256SUMS.txt` | 产物 SHA256，分发前可校验 |

在其他目录执行时用 `-File` 方式调用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Code\MailClient\packaging\build-installer.ps1
```

## 常用参数

| 参数 | 作用 |
| --- | --- |
| `-Version 1.0.1` | 指定版本号（默认读 `Directory.Build.props` 的 `<Version>`，必须是 `x.y.z`） |
| `-FrameworkDependent` | 打框架依赖版（安装包约 8 MB，目标机器需装 .NET 8 Desktop Runtime） |
| `-NoZip` | 不生成便携版 zip |
| `-SkipPublish` | 跳过 `dotnet publish`，用上次 `packaging\obj\publish` 的内容重打安装包（改 NSIS 文案时最快） |
| `-KeepClientRunning` | 客户端在运行时直接报错退出，而不是自动关闭 |
| `-MakensisPath` | 指定 `makensis.exe`（默认自动找 PATH 与 `C:\Program Files (x86)\NSIS`） |
| `-OutputDir` | 改输出目录（默认 `<仓库根>\dist`） |

示例：

```powershell
# 只重打安装包（文案 / 图标改动后），不重新发布
.\packaging\build-installer.ps1 -SkipPublish

# 小巧版，不要便携 zip
.\packaging\build-installer.ps1 -FrameworkDependent -NoZip

# 发版：显式版本号
.\packaging\build-installer.ps1 -Version 1.0.2
```

## x64 与 MSI

- `Directory.Build.props` 统一设置 `PlatformTarget=x64`；NSIS / MSI 发布都使用 `-r win-x64 --self-contained true`，产物 `ZungMail.dll` / `ZungMail.exe` 的 PE machine 为 AMD64。
- `build-msi.ps1` 默认读取 `Directory.Build.props` 的 `<Version>`（当前 `1.3.6`），产物固定为 `dist\ZungMail-1.3.6-x64.msi`。
- WiX 不要求机器预装：仓库 `.config/dotnet-tools.json` 固定 `wix` 5.0.2，脚本先执行 `dotnet tool restore`，再生成 WXS 并 `dotnet wix build -arch x64`。
- MSI 按机器安装到 `Program Files\ZungMail`，创建开始菜单与桌面快捷方式；ProductName=ZungMail、ProductVersion=1.3.6，SummaryInformation 平台为 x64。
- MSI 只包含程序文件与快捷方式，不写 `%APPDATA%\ZungMail` / `%LOCALAPPDATA%\ZungMail`；卸载不会删除用户邮件与设置。
- 验证：`msiexec /a`（管理安装解包）在本机返回 1603，不能用来校验载荷；改用「`packaging\obj\msi-publish` 文件数 == 生成 WXS 里的 `<File ` 条数」对账，脚本会自动核对 ProductVersion 与 x64 平台并重算 `SHA256SUMS.txt`。
- **安装完成后自动启动客户端**：MSI 里的 `LaunchZungMail` 自定义动作（`After="InstallFinalize"`）用 `explorer.exe` 转发启动 `ZungMail.exe`，条件为 `LAUNCHAPP=1 AND NOT REMOVE~="ALL" AND UILevel > 2` —— 卸载与 `msiexec /qn` 静默部署都不启动；运维需要连正常安装也不启动时传 `LAUNCHAPP=0`。

## 产品改名与升级兼容（MailClient → ZungMail）

1.2.0 起产品全量改名：程序集、`ZungMail.exe`、安装目录 `Program Files\ZungMail`、开始菜单程序组、
「应用和功能」条目、数据目录 `%APPDATA%\ZungMail` 全部换成新名字。打包侧对应的兼容处理：

| 场景 | 处理方式 |
| --- | --- |
| 旧版是 **MSI** 装的（MailClient 1.1.x） | `New-MsiSource.ps1` 的 `UpgradeCode` 仍是 `{D2A9F5D4-6D7B-4F7A-9C2E-71B5E3A8C6F1}`，`MajorUpgrade AllowSameVersionUpgrades="yes"` 保留 —— 1.2.0 直接原地升级，不产生并排安装 |
| 旧版是 **NSIS setup.exe** 装的 | `ZungMail.nsi` 安装段先 `taskkill` 掉 `ZungMail.exe` 与 `MailClient.exe`；只要注册表里还有 `...\Uninstall\MailClient` 就删掉该键、清 `$SMPROGRAMS\MailClient` 与 `$DESKTOP\MailClient.lnk`，并 `RMDir /r` 掉 `Program Files\MailClient`（仅当目录里确实有 `MailClient.exe` 时才删） |
| 用户数据 | 安装段**绝不**删 `%APPDATA%\MailClient` / `%LOCALAPPDATA%\MailClient`，那里可能是旧数据的备份；旧数据由客户端首次启动时接管或保留 |
| 卸载 | 只提示删除 `%APPDATA%\ZungMail` 与 `%LOCALAPPDATA%\ZungMail`，提示语里明确「旧版 MailClient 目录不会被自动删除」 |

MSI 与 NSIS 的产品标识互不相通（MSI 靠 `UpgradeCode` / `ProductCode`，NSIS 只有一条卸载注册表键），
所以升级路线要按旧版的安装形态选：

| 旧版形态 | 1.2.0 该怎么装 |
| --- | --- |
| MSI（MailClient 1.1.x） | 继续用 **MSI 1.2.0** 原地升级（推荐）：`MajorUpgrade` 会卸载旧产品，ARP 只剩一条 `ZungMail` |
| NSIS `setup.exe`（MailClient 1.1.x） | 直接跑 **setup.exe 1.2.0**：安装段会清掉旧卸载键、旧开始菜单组、旧桌面快捷方式与 `Program Files\MailClient` |
| 跨形态迁移（NSIS ↔ MSI） | 两种形态不会互相卸载：先在「应用和功能」里卸掉旧形态再装新形态。反向也一样 —— NSIS 安装器只认 NSIS 的卸载键，删不掉旧 MSI 的 ARP 条目 |

## 安全软件误杀与代码签名（重要）

典型现象：装完 `C:\Program Files\ZungMail` 里**没有 `ZungMail.exe`**，MSI 还会连带弹出
「警告 1909 无法创建快捷方式」。原因不是安装包写坏了，而是 **360 安全卫士 / 360 天擎、火绒、腾讯电脑管家**
这类启发式引擎把刚落地、且**未签名**的主程序当成可疑文件直接隔离了；主程序没了，`CreateShortcut` 自然失败。

三条应对（按优先级）：

1. **给程序签名（根治）**：`packaging/sign.ps1` 已接进两个打包脚本，配置环境变量即可自动签名
   （证书在个人证书库用指纹，或直接用 PFX）：

   ```powershell
   # 方式一：证书已装进「个人 → 证书」
   $env:ZUNGMAIL_SIGN_THUMBPRINT = 'A1B2C3....'
   # 方式二：手上有 pfx
   $env:ZUNGMAIL_SIGN_PFX = 'D:\certs\zungmail.pfx'
   $env:ZUNGMAIL_SIGN_PFX_PASSWORD = '***'

   .\packaging\build-installer.ps1    # 打包前先签 exe/dll，最后签 setup.exe
   .\packaging\build-msi.ps1          # 打包前先签 exe/dll，最后签 msi
   ```

   要求：装 Windows SDK（提供 `signtool.exe`），证书带代码签名用途；时间戳默认
   `http://timestamp.digicert.com`（可用 `ZUNGMAIL_SIGN_TIMESTAMP` 换）。没配证书时脚本只打印
   「本次产出未签名」提示，不会让打包失败。改名前的 `MAILCLIENT_SIGN_*` 变量仍然生效（新名优先，
   两套都配时以 `ZUNGMAIL_SIGN_*` 为准），已有流水线不用改。
2. **加白名单（客户机上马上可用）**：把安装目录（默认 `C:\Program Files\ZungMail`）与安装包本身加入
   安全软件信任区；360 安全卫士是「木马查杀 → 恢复区 → 恢复并信任」+「信任区」添加目录；企业天擎/火绒企业版
   由管理员下发「软件白名单 / 信任目录」。
3. **提交误报**：把 `dist\SHA256SUMS.txt` 里的哈希、安装包和程序 exe 一起提交给对应厂商的误报申诉入口
   （360 开放平台、腾讯电脑管家误报反馈、火绒论坛），说明是自研企业内部工具、签署者信息。

安装器侧的自检：NSIS 在文件复制完成后会检查 `ZungMail.exe` 是否存在，缺失时直接给出
「安全软件可能隔离了主程序」的中文提示并以退出码 1 结束（不再让用户面对看不懂的错误码）；
MSI 没有可自定义的对话框，只能靠 `%TEMP%\MSI*.log`（`MsiLogging` 默认开启）定位。

## 依赖

- **.NET SDK 8**（`global.json` 锁定 8.0.404，`dotnet --list-sdks` 能列出即可）
- **WiX Toolset 5.0.2**：仅 MSI 需要；已写入 `.config/dotnet-tools.json`，执行 `dotnet tool restore` 即可，不要求全机器安装。
- **NSIS 3**（默认 `C:\Program Files (x86)\NSIS`）。只打 zip 不装 NSIS 的话用 `-NoZip` 之外的方式不适用——脚本一定会调用 `makensis`；不想装 NSIS 可以单独跑 `dotnet publish` 后压缩 `packaging\obj\publish`。
- 自包含发布需要 `Microsoft.WindowsDesktop.App.Runtime.win-x64` 运行时包；本机 NuGet 缓存里已有，离线可用。

## 安装包行为（`ZungMail.nsi`）

- 安装范围：**所有用户**（`Program Files\ZungMail`），安装时提权一次，卸载信息写入 HKLM 的「应用和功能」。
- 覆盖升级：安装前自动结束正在运行的 `ZungMail.exe`（先发关闭消息，再强制结束）；草稿由应用自动保存。
- 从旧版 MailClient 升级：检测到旧 NSIS 卸载键就顺手清掉旧安装（卸载键、开始菜单程序组、桌面快捷方式、`Program Files\MailClient`），用户数据目录一律不动；细节见上文「产品改名与升级兼容」。
- **安装完成后自动启动客户端**：客户端内点「立即安装」会用 `/S` 静默启动本安装包，装好后自动把 ZungMail 打开（`explorer.exe` 转发，客户端不会继承管理员令牌）；手动双击走向导时仍由「完成」页的「立即运行 ZungMail」负责，静默安装若发现 `ZungMail.exe` 没写进去会弹窗报错并以退出码 1 结束。
- 勾选项：主程序（必选）、桌面快捷方式；开始菜单固定创建 `ZungMail` 程序组与卸载入口。
- 环境检查（不阻止安装，只提示）：未装 WebView2 运行时会说明「正文将退化为纯文本」；框架依赖版额外检查 .NET 8 桌面运行时。
- 卸载：删除程序目录、快捷方式与注册表项；**默认保留** `%APPDATA%\ZungMail`（邮件库 / 附件 / 设置）与 `%LOCALAPPDATA%\ZungMail`，弹窗选「是」才一并删除；旧版 `MailClient` 的数据目录不会被卸载器碰。

## 自定义

| 想改什么 | 改哪里 |
| --- | --- |
| 产品名、发布者、安装目录 | `packaging/ZungMail.nsi` 顶部的 `!define APP_NAME / APP_PUBLISHER / InstallDir` |
| 欢迎页文案、完成页是否自动运行 | `MUI_WELCOMEPAGE_TEXT`、`MUI_FINISHPAGE_RUN*` |
| 图标 | `src/ZungMail.App/Assets/Brand/ZungMail.ico`（同时用于 exe 与安装包） |
| 版本号 | `Directory.Build.props` 的 `<Version>`，或构建时 `-Version` |
| 安装包文件名 | `packaging/build-installer.ps1` 里的 `$setupName` |

## Visual Studio / VS Code

- VS 的「发布」向导可用项目内的 `src/ZungMail.App/Properties/PublishProfiles/win-x64-selfcontained.pubxml`（与脚本同参数：win-x64、自包含、非单文件）。
- VS Code：`终端 → 运行任务… → package-installer` 可直接调用本脚本。

## `dotnet` 命令在本机崩溃（0xC0000005）

症状：`dotnet build` / `dotnet test` / `dotnet publish` 直接以 **-1073741819（0xC0000005）** 退出，堆栈里能看到
`System.ServiceProcess.ServiceController.GetServices` → `Microsoft.DotNet.Installer.Windows.WindowsUpdateAgent`。

原因不在仓库：CLI 启动的后台 workload 广告线程会枚举本机 Windows 服务，而 .NET 的 `EnumServicesStatusEx` 编组在某个服务记录上越界（常见于残留/畸形服务项），整个进程被带走。重装 SDK、设 `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1` 都无效（管理员删掉残留服务项或重启可清掉，但普通用户做不了）。

打包脚本已内置兼容层 `packaging\dotnet-cli.ps1`：先按原样调用 CLI，**只有**在退出码是该崩溃码时才自动退回
`dotnet exec "<sdk>\MSBuild.dll" -t:Publish` 或缓存里的 `wix.dll`（`dotnet exec <dll>` 不会启动广告线程）。
健康机器上行为完全不变。手动构建/测试时同样可用：

```powershell
$sdk = "C:\Program Files\dotnet\sdk\8.0.404"
dotnet exec "$sdk\MSBuild.dll" ZungMail.sln -restore -p:Configuration=Release -m:1
dotnet exec "$sdk\vstest.console.dll" tests\ZungMail.Tests\bin\Release\net8.0\ZungMail.Tests.dll
```

## 常见问题

**`dotnet publish` 报文件被占用（MSB3021 / MSB3027）**
客户端还在跑。脚本默认会自动关掉它；如果看到这条说明是加了 `-KeepClientRunning`，或者关闭失败——手动结束 `ZungMail.exe` 再打包。

**makensis 报 `Bad text encoding`**
`ZungMail.nsi` 必须保存为 **UTF-8 with BOM**（NSIS 只认 ANSI 或带 BOM 的 UTF-8）。用 VS Code 另存时确认编码，或在 PowerShell 里：

```powershell
$p = 'packaging\ZungMail.nsi'
$t = [IO.File]::ReadAllText($p, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($p, $t, [Text.UTF8Encoding]::new($true))
```

**升级后客户端启动报错 / 数据异常**
安装包只覆盖程序文件，不动 `%APPDATA%\ZungMail`。若怀疑安装目录残留旧版本，卸载后重装即可；卸载默认保留数据。
