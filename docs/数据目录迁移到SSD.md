# 把数据目录搬到 SSD（启动与检索提速）

适用范围：数据目录放在机械硬盘（如 `D:\MailClient`、`D:\ZungMail`）的机器。
典型症状：升级后首次启动重建索引用时 2~3 分钟、列表首屏慢、搜索卡、存储占用统计慢。

判断是不是这个原因：日志（`%APPDATA%\ZungMail\logs\`）里出现

```
启动环境：数据目录=D:\MailClient / 磁盘=D:\ 可用 … / mail.db 2937MB
启动阶段：数据库 建表索引 163501ms
```

`mail.db` 几百 MB 到几 GB、且所在磁盘是 7200 rpm 机械盘（SATA HDD）时，SQLite 的随机 I/O
会被放大一个数量级；搬到 SSD 后同一份库通常快 5~20 倍。

---

## 一、先确认没有程序在跑

任务栏退出 ZungMail（托盘图标右键 → 退出），然后确认：

```powershell
Get-Process ZungMail -ErrorAction SilentlyContinue   # 应该没输出
```

## 二、备份（必须，5 分钟）

```powershell
# 旧目录（按实际情况改）
$old = 'D:\MailClient'
$bak = 'D:\MailClient-backup-20261025'

robocopy $old $bak /MIR /R:1 /W:1 /XD WebView2Preview /NFL /NDL
```

`mail.db` 是 WAL 模式，退出客户端后 `mail.db-wal` / `mail.db-shm` 会被合并，**必须在客户端完全退出后复制**，
否则拿到的是半套数据（打开时报 `database is locked` 或 `SQLITE_CORRUPT`）。

## 三、复制到 SSD

```powershell
$old = 'D:\MailClient'
$new = 'C:\ZungMailData'          # 目标放在系统盘 SSD 上，留足空间

robocopy $old $new /MIR /R:1 /W:1 /NFL /NDL
```

复制完先核对大小（两个数字应该接近，差几十 MB 属正常，因为 WAL 会合并）：

```powershell
'{0:N0} MB' -f ((Get-ChildItem $old -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
'{0:N0} MB' -f ((Get-ChildItem $new -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
```

## 四、切换数据目录

打开 ZungMail → **设置 → 数据 → 数据目录**，改成 `C:\ZungMailData`，保存后重启客户端。

客户端会把选择写进 `%APPDATA%\ZungMail\datadir.txt` 指针；下次启动直接读新路径。

> 如果界面上没有「更改目录」入口（旧版本），可以手工改指针文件：把
> `C:\ZungMailData` 一行写进 `%APPDATA%\ZungMail\datadir.txt`（UTF-8，无 BOM），再重启客户端。

## 五、验证

重启后看日志：

```powershell
Select-String -Path "$env:APPDATA\ZungMail\logs\app-*.log" -Pattern '启动环境：数据目录|启动阶段：数据库初始化' |
  Select-Object -Last 4
```

- `数据目录=C:\ZungMailData / 磁盘=C:\`
- `数据库初始化` 应当是毫秒级（结构已是 v24，不会再迁移）
- 列表首屏、搜索、存储占用应明显变快

确认账户数、邮件总数、标签、规则、等待回复与升级前一致后，再删旧目录：

```powershell
Remove-Item 'D:\MailClient' -Recurse -Force
```

## 六、回滚

数据目录只影响"从哪里读"，不改变库结构。想回退：把 `%APPDATA%\ZungMail\datadir.txt`
改回 `D:\MailClient` 即可（前提是旧目录还没删）。

## 七、顺带能省的另一个大头

进度窗口里最慢的一步是「会话归并索引（全表重建）」，它只在**升级那一次**发生。
已经升级到 v24 的机器不会再触发；后续升级如果又出现慢启动，先看日志里的
`数据库 建表索引` 与 `列表 填行 / 列表 选中 / 列表上屏` 三段，哪一段大就查哪一段。
