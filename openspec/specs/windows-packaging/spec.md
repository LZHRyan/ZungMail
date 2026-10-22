# windows-packaging Specification

## Purpose
让 ZungMail 的 Windows 构建固定为 x64，并提供可由企业软件分发系统静默安装的 MSI 安装包；安装只写入程序目录与快捷方式，用户邮件数据保持不变。

## Requirements

### Requirement: 客户端按 x64 编译

ZungMail 的 Windows 客户端及其依赖 SHALL 以 x64 为目标平台编译；构建产物中的托管程序集与可执行文件 SHALL 标记为 AMD64，而不是 AnyCPU 或 x86。

#### Scenario: Release 构建产物为 x64

- **WHEN** 在仓库根目录执行 Release 构建
- **THEN** `ZungMail.dll` 与 `ZungMail.exe` 的 PE machine 为 `Amd64`，发布命令使用 `win-x64` 运行时

#### Scenario: 测试仍可运行

- **WHEN** x64 配置下运行客户端 / 界面测试
- **THEN** 测试进程以 x64 启动并通过，不因平台位宽变化出现 `BadImageFormatException`

### Requirement: 生成 1.0.0 的 MSI 安装包

打包脚本 SHALL 从自包含的 x64 publish 输出生成 `ZungMail-1.0.0-x64.msi`；MSI 的 ProductVersion SHALL 为 `1.0.0`，并包含运行所需的全部程序文件、依赖与 `Assets/help/user-guide.html`。

#### Scenario: 完整发布内容进入 MSI

- **WHEN** 执行 MSI 打包脚本
- **THEN** 产物位于 `dist/ZungMail-1.0.0-x64.msi`，管理安装 / 解包后能看到 `ZungMail.exe`、运行时依赖、`Assets/help/user-guide.html` 等发布文件

#### Scenario: 版本号固定

- **WHEN** 查询 MSI 的 ProductVersion
- **THEN** 返回 `1.0.0`，文件名与 ProductName 也包含该版本

#### Scenario: 可复现的 WiX 工具

- **WHEN** 在未预装 WiX 的机器上 clone 仓库并执行 `dotnet tool restore`
- **THEN** 本地工具清单恢复 `wix` 5.0.2，MSI 脚本可直接构建，不需要手工安装 WiX 或点击 GUI

### Requirement: MSI 安装与卸载行为

MSI SHALL 按机器安装到 `Program Files\ZungMail`，写入「应用和功能」并创建开始菜单快捷方式；卸载 SHALL 删除程序文件与快捷方式，但 MUST NOT 删除 `%APPDATA%\ZungMail` 或 `%LOCALAPPDATA%\ZungMail` 中的用户数据。

#### Scenario: 安装后可从开始菜单启动

- **WHEN** 用户完成 MSI 安装
- **THEN** `Program Files\ZungMail\ZungMail.exe` 存在，开始菜单出现 ZungMail 快捷方式，ARP 显示名称与版本 1.0.0

#### Scenario: 卸载保留邮件数据

- **WHEN** 用户在「应用和功能」卸载 ZungMail
- **THEN** 安装目录与快捷方式被删除，`%APPDATA%\ZungMail` 与 `%LOCALAPPDATA%\ZungMail` 仍保留

#### Scenario: 覆盖升级

- **WHEN** 安装更高版本的 MSI 且旧版本已安装
- **THEN** MajorUpgrade 先移除旧程序文件再安装新版本，不要求用户先手工卸载
