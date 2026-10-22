namespace ZungMail.Core.Domain;

/// <summary>应用设置（与设置界面一一对应，JSON 持久化）。</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "light";           // light | blue | pink | orange（暗色模式已下线）
    public string FontSize { get; set; } = "normal";        // compact | normal | large
    public string Language { get; set; } = "zh-CN";

    public bool ConversationDefault { get; set; }

    /// <summary>当前 Windows 用户登录后自动启动 ZungMail。</summary>
    public bool LaunchOnStartup { get; set; }

    public bool CloseToTray { get; set; } = true;
    public bool DesktopNotify { get; set; } = true;

    /// <summary>用户设置的同步间隔，<see cref="ZungMail"/> 的默认文件夹映射参考此值。</summary>
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool SyncOnLaunch { get; set; } = true;

    /// <summary>同步间隔的有效值：夹取到 1–120 分钟，避免配置异常导致狂刷服务器或永不收信。</summary>
    public static int EffectiveSyncIntervalMinutes(int minutes) => Math.Clamp(minutes, 1, 120);

    /// <summary>
    /// 正文预取窗口（天）：同步时只自动下载最近 N 天邮件的正文与附件，
    /// 更早的邮件只同步服务器上的邮件头，选中该邮件时再按需下载正文与附件（NFR-P4 / FR-03）。
    /// 0 表示完全不预取，等价于「只同步邮件头」。
    /// </summary>
    public int BodyPrefetchDays { get; set; } = 7;

    /// <summary>正文预取窗口取该值时表示不限日期（全部邮件都自动下载正文）。</summary>
    public const int BodyPrefetchAllDays = int.MaxValue;

    /// <summary>
    /// 把正文预取天数换算成窗口起点（UTC）。返回 null 表示不预取（天数 ≤ 0）。
    /// 解析出的起点直接用于 <c>date_utc &gt;= 起点</c> 的查询，与仓储的 UTC ISO 存储格式一致。
    /// </summary>
    public static DateTimeOffset? BodyPrefetchFloor(int days, DateTimeOffset now)
    {
        if (days <= 0) return null;
        return days >= BodyPrefetchAllDays ? DateTimeOffset.MinValue : now.AddDays(-days);
    }

    public bool ClearBodyCacheOnExit { get; set; }
    public int KeepTrashDays { get; set; } = 30;

    /// <summary>列表分页大小，禁止一次性全量读取。</summary>
    public int ListPageSize { get; set; } = 100;

    /// <summary>
    /// 旧版全局纯文本签名。新版按账户配置富文本签名（<see cref="Signatures"/>），
    /// 这里只作为「从未在签名页保存过」的账户兜底，保证升级后老签名不丢。
    /// </summary>
    public string Signature { get; set; } = "";

    /// <summary>按邮箱账户配置的富文本签名。</summary>
    public List<AccountSignature> Signatures { get; set; } = new();

    /// <summary>取账户的签名条目；该账户未单独设置时返回 null。</summary>
    public AccountSignature? FindSignature(long accountId)
        => Signatures.FirstOrDefault(s => s.AccountId == accountId);

    /// <summary>
    /// 解析账户实际生效的签名 HTML：单独设置优先（未勾选启用则为空），
    /// 没有单独设置时回退旧版全局签名。
    /// </summary>
    public string ResolveSignatureHtml(long accountId)
    {
        if (FindSignature(accountId) is { } entry)
        {
            return entry.IsEnabled ? entry.Html ?? "" : "";
        }

        return string.IsNullOrWhiteSpace(Signature) ? "" : Services.HtmlTextExtractor.ToHtml(Signature.Trim());
    }

    // ---------------- 正文渲染 ----------------

    /// <summary>使用 HTML 渲染邮件正文；关闭后回退为纯文本。</summary>
    public bool HtmlRendering { get; set; } = true;

    /// <summary>默认阻止远程图片（防追踪像素），用户可在正文顶部手动加载。</summary>
    public bool BlockRemoteImages { get; set; } = true;

    /// <summary>
    /// 超长邮件（多层引用的长线程）默认把引用的历史邮件折叠起来，点一下再展开。
    /// 关闭后历史引用直接展开显示，只做缩进压平。
    /// </summary>
    public bool FoldQuotedHistory { get; set; } = true;

    /// <summary>
    /// 折叠时保留最近几层引用直接显示，更早的才收起（设置里可选 5–10，默认 5）。
    /// 引用层数不超过这个值时整篇保持展开。
    /// </summary>
    public int FoldQuotedHistoryKeepLayers { get; set; } = 5;

    /// <summary>附件单击优先使用内置预览（PDF / 图片 / 文本）；关闭后改用本机程序打开。</summary>
    public bool UseInternalAttachmentPreview { get; set; } = true;

    /// <summary>阅读窗格右侧的“邮件上下文”侧边栏是否展开。</summary>
    public bool ShowContextSidebar { get; set; } = true;

    /// <summary>左侧栏「快捷视图」分区是否展开（折叠后只留标题条，状态随设置保存）。</summary>
    public bool SidebarQuickViewsExpanded { get; set; } = true;

    /// <summary>左侧栏「标签」分区是否展开（折叠后只留标题条，状态随设置保存）。</summary>
    public bool SidebarTagsExpanded { get; set; } = true;

    /// <summary>
    /// 「全部搜索条件」面板里隐藏的条件（逗号分隔的字段键：from,to,subject,body,attachment,tag,reminder,note,time）。
    /// 空串 = 全部显示，与 Foxmail 的「+」菜单默认全勾选一致。
    /// </summary>
    public string SearchFilterHiddenFields { get; set; } = "";

    /// <summary>
    /// 空闲时按邮箱地址查 Gravatar 显示联系人头像（默认开）。
    /// 每次运行只尝试一次、最多 300 个地址；关闭后一律用首字母色块。
    /// 注意：查询会把邮箱地址的 MD5 发给 gravatar.com。
    /// </summary>
    public bool LoadContactAvatars { get; set; } = true;

    /// <summary>收到请求时自动发送阅读收条（默认关闭，避免泄露阅读行为）。</summary>
    public bool AutoSendReadReceipt { get; set; }

    /// <summary>
    /// 在列表与阅读区标记陌生发件人（首次来信，默认开）。
    /// 关闭只影响展示：判定结论仍随邮件写入本地库，重新开启后立刻恢复显示。
    /// </summary>
    public bool MarkStrangerSenders { get; set; } = true;

    /// <summary>
    /// 检测仿冒域名（钓鱼警示，默认开）：发件人域名与本机自有企业域名高度相似时标红提示。
    /// 判定在渲染时计算，所以开关与容差改动会立即作用于列表中的历史邮件。
    /// </summary>
    public bool PhishingDomainWarning { get; set; } = true;

    /// <summary>仿冒域名相似度容差：1 = 只差一个字符即命中（默认），2 = 放宽到两个字符（更易误报）。</summary>
    public int PhishingDomainTolerance { get; set; } = Services.PhishingDomainPolicy.DefaultTolerance;

    // ---------------- 写信窗口 ----------------

    /// <summary>
    /// 写信窗口右侧「快速文本 / 地址簿」侧栏是否展开。记住上一次的开关状态，
    /// 下次写邮件沿用，免得每次都要手动打开。
    /// </summary>
    public bool ComposeQuickTextPanelVisible { get; set; }

    // ---------------- 客户端锁定 ----------------

    /// <summary>是否启用客户端锁定（需先设置密码）。</summary>
    public bool ClientLockEnabled { get; set; }

    /// <summary>锁定口令校验串（PBKDF2 结果再经 DPAPI 保护）；为空表示未设置密码。</summary>
    public string ClientLockSecret { get; set; } = "";

    /// <summary>空闲多少分钟后自动锁定；0 表示只允许手动锁定。</summary>
    public int AutoLockMinutes { get; set; }

    // ---------------- 全局热键 ----------------

    /// <summary>是否注册系统级热键用于显示/隐藏主界面。</summary>
    public bool GlobalHotkeyEnabled { get; set; }

    /// <summary>全局热键组合，默认 Ctrl+Alt+M。</summary>
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+M";

    // ---------------- 存储监控与清理 ----------------

    /// <summary>是否启用磁盘空间分级预警。</summary>
    public bool StorageAlertsEnabled { get; set; } = true;

    /// <summary>提示级阈值（百分比）。</summary>
    public int StorageNoticePercent { get; set; } = 75;

    /// <summary>警告级阈值（百分比）。</summary>
    public int StorageWarningPercent { get; set; } = 85;

    /// <summary>严重级阈值（百分比）。</summary>
    public int StorageCriticalPercent { get; set; } = 95;

    /// <summary>邮件存储目录容量上限（字节），0 表示按磁盘总容量计算占用比例。</summary>
    public long StorageLimitBytes { get; set; }

    /// <summary>清理区保留天数：到期自动彻底删除并释放空间，期间可撤销。</summary>
    public int CleanupQuarantineDays { get; set; } = 30;

    /// <summary>是否启用自动定期清理。</summary>
    public bool AutoCleanupEnabled { get; set; }

    /// <summary>自动清理间隔（小时）。</summary>
    public int AutoCleanupIntervalHours { get; set; } = 24;

    /// <summary>只清理早于该天数的邮件。</summary>
    public int AutoCleanupRetentionDays { get; set; } = 365;

    /// <summary>自动清理是否包含垃圾箱。</summary>
    public bool AutoCleanupIncludeTrash { get; set; } = true;

    /// <summary>自动清理是否包含广告邮件。</summary>
    public bool AutoCleanupIncludeJunk { get; set; } = true;

    // ---------------- 运行日志 ----------------

    /// <summary>
    /// 运行日志保留天数（1–7 天，默认 7）：<c>logs\app-日期.log</c> 超期后由后台自动删除。
    /// 范围与判定边界统一由 <see cref="LogRetentionPolicy"/> 决定。
    /// </summary>
    public int LogRetentionDays { get; set; } = LogRetentionPolicy.DefaultDays;

    /// <summary>日志保留天数的有效值：夹取到 1–7 天，避免配置异常导致永不清理或当天就被删。</summary>
    public static int EffectiveLogRetentionDays(int days) => LogRetentionPolicy.Normalize(days);

    // ---------------- 货代行业功能（默认全开；关闭后不再识别、入口隐藏，存量数据保留） ----------------

    /// <summary>货代行业功能总开关；关闭时下面四项一律按关闭处理。</summary>
    public bool FreightFeaturesEnabled { get; set; } = true;

    /// <summary>业务编号识别（Job / SO / B/L / 箱号 / 封号 / 船名 / 港口 …）。</summary>
    public bool FreightReferencesEnabled { get; set; } = true;

    /// <summary>行业邮件类型识别（订舱确认 / SI / VGM / 提单草稿 / AN / 账单 / 查验 / 甩柜 / 延误）。</summary>
    public bool FreightMailTypesEnabled { get; set; } = true;

    /// <summary>时效节点抽取（截关 / 截补料 / VGM / ETD / ETA）。</summary>
    public bool FreightDeadlinesEnabled { get; set; } = true;

    /// <summary>「等待回复」跟进与到点提醒。</summary>
    public bool FreightFollowUpsEnabled { get; set; } = true;

    // ---------------- 在线升级 ----------------

    /// <summary>
    /// 升级服务根地址（域名 / IP / 完整 URL，可带端口与路径前缀）。
    /// 为空时回退到安装目录的 update.json；两者都没有则视为「未配置升级服务」。
    /// </summary>
    public string UpdateServiceUrl { get; set; } = "";

    /// <summary>升级渠道：默认稳定版，可在设置里切到 beta。</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>启动后是否自动检测新版本（只检测，不自动下载或安装）。</summary>
    public bool UpdateAutoCheckEnabled { get; set; } = true;

    /// <summary>启动后多少分钟开始自动检测；界面提供 1/3/5/10/30 分钟档位。</summary>
    public int UpdateAutoCheckDelayMinutes { get; set; } = 3;

    /// <summary>自动检测延迟的有效值：夹取到 1–30 分钟，避免配置异常导致永不检测或立刻打扰。</summary>
    public static int EffectiveUpdateDelayMinutes(int minutes)
        => Math.Clamp(minutes <= 0 ? 3 : minutes, 1, 30);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>同步进度（用于状态栏与进度条）。</summary>
public sealed class SyncProgress
{
    public long AccountId { get; init; }
    public string FolderName { get; init; } = "";
    public SyncPhase Phase { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }

    /// <summary>百分比，0 表示不确定进度。</summary>
    public double Percent => Total <= 0 ? 0 : Math.Round(Processed * 100.0 / Total, 1);

    public bool IsIndeterminate => Total <= 0;

    public string Text => Phase switch
    {
        SyncPhase.Connecting => "正在连接服务器…",
        SyncPhase.Folders => "正在同步文件夹列表…",
        SyncPhase.Headers => Total > 0 ? $"正在同步 {FolderName} {Processed}/{Total}" : $"正在同步 {FolderName}…",
        SyncPhase.Flags => "正在校准已读状态…",
        SyncPhase.Bodies => $"正在下载正文 {Processed}/{Total}",
        SyncPhase.Completed => "同步完成",
        SyncPhase.Failed => "同步失败",
        SyncPhase.Cancelled => "同步已取消",
        _ => "就绪"
    };
}

/// <summary>单个账户一次同步的结果。</summary>
public sealed class SyncOutcome
{
    public long AccountId { get; init; }
    public string AccountName { get; init; } = "";
    public int FoldersSynced { get; init; }
    public int NewMessages { get; init; }
    public int UpdatedFlags { get; init; }
    public bool FullResync { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }

    public bool Success => string.IsNullOrEmpty(Error);

    public string Summary => Success
        ? $"同步完成：{FoldersSynced} 个文件夹，新增 {NewMessages} 封，状态更新 {UpdatedFlags} 条"
        : $"同步失败：{Error}";
}

/// <summary>单个账户的签名设置（富文本 HTML + 是否启用）。</summary>
public sealed class AccountSignature
{
    public long AccountId { get; set; }

    /// <summary>是否在写信时追加该账户的签名。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>签名 HTML（与模板编辑区同一套富文本，可含加粗、颜色、图片等）。</summary>
    public string Html { get; set; } = "";
}

