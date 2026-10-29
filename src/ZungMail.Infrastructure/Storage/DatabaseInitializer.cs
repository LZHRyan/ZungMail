using System.Globalization;
using System.Text;
using ZungMail.Core.Services;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 数据库初始化：建表、索引、FTS5 全文索引与同步触发器（全部 IF NOT EXISTS，可重复执行），
/// 并按 <see cref="Migrations"/> 有序推进架构版本。
/// </summary>
public sealed class DatabaseInitializer
{
    /// <summary>当前架构版本；每次结构变更递增，并在 <see cref="Migrations"/> 注册对应步骤。</summary>
    public const int SchemaVersion = 27;

    /// <summary>一次性修复：早期版本未落盘 multipart/related 内嵌图片，正文残留 cid: 引用。</summary>
    private const string InlineImageRepairKey = "repair.inline-images.v1";

    /// <summary>一次性回填：v15 起新增时效节点与一票货案件，需要把已有邮件重新抽一遍。</summary>
    private const string ShipmentBackfillKey = "backfill.shipment-cases.v1";

    /// <summary>一次性回填：v17 起新增行业邮件类型与案件阶段，需要再重抽一遍。</summary>
    private const string MailTypeBackfillKey = "backfill.mail-types.v2";

    /// <summary>一次性回填：v23 起新增退信标识，历史退信也要标出来（不等下一次同一个人再退）。</summary>
    private const string BounceBackfillKey = "backfill.bounce-marks.v1";

    /// <summary>
    /// 有序迁移表：版本号必须连续递增。每个迁移在独立事务中执行，成功后连同版本号一起提交；
    /// 失败则整体回滚，下次启动可从原版本重试。新增结构变更时在此追加一项并递增 <see cref="SchemaVersion"/>。
    /// </summary>
    private static readonly (int Version, string Name, Action<SqliteConnection, SqliteTransaction> Apply)[] Migrations =
    {
        // 版本 1：初始结构由 SchemaSql 建立，此处仅作为基线占位。
        (1, "InitialSchema", static (_, _) => { }),

        // 版本 2：失败退避时间 + 入队即固定的 Message-ID。
        (2, "OutboxRetryAndMessageId", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "outbox", "next_attempt_utc", "TEXT");
            EnsureColumn(connection, tx, "outbox", "message_id", "TEXT");
        }),

        // 版本 3：本地优先写操作的待同步队列（已读 / 星标 / 删除）。
        (3, "PendingOperations", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = PendingOperationSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 4：早期未注册代码页时，少量俄文/中文主题以 Latin-1 乱码写入本地库；安全修复可识别的标题并同步 FTS。
        (4, "RepairLegacySubjectEncoding", static (connection, tx) => RepairLegacySubjects(connection, tx)),

        // 版本 5：修正 v4 中极少数 GBK 乱码被错误按 CP1251 解读后形成的伪俄文标题。
        (5, "RepairMisclassifiedGbkSubjects", static (connection, tx) => RepairMisclassifiedGbkSubjects(connection, tx)),

        // 版本 6：本地邮件规则（过滤器）。
        (6, "MailRules", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = MailRuleSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 7：邮件标签与待办（本地业务状态，不回写 IMAP）。
        (7, "TagsAndTodos", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = TagTodoSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 8：待办到期提醒去重标记。
        (8, "MailTodoReminded", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "mail_todos", "reminded_utc", "TEXT");
        }),

        // 版本 9：邮件备注（本地批注，不上传服务器）。
        (9, "MessageNotes", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
CREATE TABLE IF NOT EXISTS message_notes(
    email_id   INTEGER PRIMARY KEY,
    note       TEXT NOT NULL DEFAULT '',
    updated_utc TEXT NOT NULL
);
""";
            command.ExecuteNonQuery();
        }),

        // 版本 10：阅读收条请求地址（Disposition-Notification-To）。
        (10, "MessageMdnTo", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "messages", "mdn_to", "TEXT");
        }),

        // 版本 11：阅读收条发送记录（同一封只提示一次）。
        (11, "ReadReceipts", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
CREATE TABLE IF NOT EXISTS read_receipts(
    email_id INTEGER PRIMARY KEY,
    sent_utc TEXT NOT NULL
);
""";
            command.ExecuteNonQuery();
        }),

        // 版本 12：规则「命中后继续执行后续规则」（对齐 Foxmail 过滤器的同名选项）。
        (12, "MailRuleContinueAfterMatch", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "mail_rules", "continue_after_match", "INTEGER NOT NULL DEFAULT 0");
        }),

        // 版本 13：本地发件人黑白名单（垃圾邮件判定，不改服务器状态）。
        (13, "SenderFilters", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
CREATE TABLE IF NOT EXISTS sender_filters(
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    pattern      TEXT NOT NULL,
    is_blacklist INTEGER NOT NULL DEFAULT 1,
    note         TEXT NOT NULL DEFAULT '',
    created_utc  TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sender_filters_pattern
    ON sender_filters(pattern, is_blacklist);
""";
            command.ExecuteNonQuery();
        }),

        // 版本 14：货代业务标识（Job / SO / B/L / 箱号 / 船名航次 …）与扫描进度，
        // 支撑「一票货」聚合、结构化搜索（booking:/bl:/ctr:）与业务字段展示。
        (14, "FreightReferences", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = FreightReferenceSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 15：货代时效节点（截关 / 截补料 / VGM / 单证 / ETD / ETA）与「一票货」案件聚合。
        // message_deadlines 支撑倒计时看板；shipment_cases 把同一票货的邮件、附件、节点聚在一页。
        (15, "ShipmentCasesAndDeadlines", static (connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = ShipmentCaseSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 16：等待回复闭环（mail_followups）+ 规则动作「打标签」与「发件域」条件。
        (16, "FollowUpsAndRuleAddTag", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "mail_rules", "from_domain", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, tx, "mail_rules", "action_arg", "TEXT NOT NULL DEFAULT ''");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = FollowUpSchemaSql;
            command.ExecuteNonQuery();
        }),

        // 版本 17：行业邮件类型（订舱确认 / SI / VGM / 提单草稿 / AN / 账单 / 查验 / 甩柜 / 延误）
        // 与由类型推进的案件阶段。
        (17, "MailTypesAndCaseStage", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "messages", "mail_type", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, tx, "messages", "mail_type_score", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, tx, "shipment_cases", "stage", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, tx, "shipment_cases", "exception", "INTEGER NOT NULL DEFAULT 0");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "CREATE INDEX IF NOT EXISTS ix_messages_mail_type ON messages(mail_type);";
            command.ExecuteNonQuery();
        }),

        // 版本 18：标签快捷键从固定「Ctrl+数字」升级为可录制的组合键（Ctrl/Alt/Shift/Win + 字母/数字/F 键）。
        // 旧编码 1..9 / 11..19 回填成文本，未绑定的保持 NULL。
        (18, "TagShortcutGesture", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "mail_tags", "shortcut_gesture", "TEXT");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
UPDATE mail_tags
SET shortcut_gesture = CASE
        WHEN shortcut BETWEEN 1 AND 9 THEN 'Ctrl+' || shortcut
        WHEN shortcut BETWEEN 11 AND 19 THEN 'Ctrl+Shift+' || (shortcut - 10)
        ELSE NULL
    END
WHERE shortcut_gesture IS NULL AND shortcut IS NOT NULL;
""";
            command.ExecuteNonQuery();
        }),

        // 版本 19：标签改为「按账户隔离 + 单键打/取消」。
        // 旧标签归到第一个账户；旧组合键（shortcut_gesture）语义已作废，统一清空；
        // 名称唯一性从全局改成「账户内唯一」，键位也只在同一账户内查重。
        (19, "TagPerAccountAndSingleKey", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "mail_tags", "account_id", "INTEGER");
            EnsureColumn(connection, tx, "mail_tags", "shortcut_key", "TEXT");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
UPDATE mail_tags
SET account_id = (SELECT id FROM accounts ORDER BY sort_order, id LIMIT 1)
WHERE account_id IS NULL;

UPDATE mail_tags SET shortcut_gesture = NULL WHERE shortcut_gesture IS NOT NULL;

DROP INDEX IF EXISTS ux_mail_tags_name;
CREATE UNIQUE INDEX IF NOT EXISTS ux_mail_tags_account_name ON mail_tags(account_id, name);
""";
            command.ExecuteNonQuery();
        }),

        // 版本 20：账户级收取策略（完整收取 / 仅收取正文 / 仅收取邮件头）。
        // 既有账户统一置 0（继承全局「默认收取策略」），升级后行为与升级前一致。
        (20, "AccountFetchPolicy", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "accounts", "fetch_policy", "INTEGER NOT NULL DEFAULT 0");
        }),

        // 版本 21：主账户标记。同步顺序与列表排序都以主账户优先；存量库把第一个启用账户设为主账户。
        (21, "AccountPrimaryFlag", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "accounts", "is_primary", "INTEGER NOT NULL DEFAULT 0");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
UPDATE accounts
SET is_primary = 1
WHERE id = COALESCE(
    (SELECT id FROM accounts WHERE is_enabled = 1 ORDER BY sort_order, id LIMIT 1),
    (SELECT id FROM accounts ORDER BY sort_order, id LIMIT 1));
""";
            command.ExecuteNonQuery();
        }),

        // 版本 22：陌生发件人标识。存量邮件一律 0（非陌生），只有升级后新收到的来信参与判定；
        // from_email 建 NOCASE 索引，判定时按地址查历史来信才不会全表扫（库已是万级邮件）。
        (22, "StrangerSender", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "messages", "is_stranger", "INTEGER NOT NULL DEFAULT 0");

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_messages_from_email ON messages(from_email COLLATE NOCASE);";
            command.ExecuteNonQuery();
        }),

        // 版本 23：退信标识。存量邮件先加列，再用与收信同一份 BounceMailPolicy 回填一遍——
        // 升级前收到的退信同样需要标出来（否则要等下次同一个人再退一次才生效）。
        // 细节三列由正文下载后的 delivery-status 解析回填，未解析时保持空串。
        (23, "BounceMark", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "messages", "is_bounce", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, tx, "messages", "bounce_recipient", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, tx, "messages", "bounce_status", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, tx, "messages", "bounce_detail", "TEXT NOT NULL DEFAULT ''");
        }),

        // 版本 24：会话归并键。会话列表原来按 COALESCE(NULLIF(thread_id,''),'local:'||id) 分组，
        // 表达式不 sargable、每页还要按组回查一次全表；改成虚拟生成列 + 索引，查询与分组都能走索引。
        (24, "ThreadKeyIndex", static (connection, tx) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                // 生成列在 pragma_table_info 里是隐藏列，必须用 table_xinfo 才能看到
                check.CommandText = "SELECT COUNT(*) FROM pragma_table_xinfo('messages') WHERE name = 'thread_key';";
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using var alter = connection.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE messages ADD COLUMN thread_key TEXT " +
                        "GENERATED ALWAYS AS (COALESCE(NULLIF(thread_id, ''), 'local:' || id)) VIRTUAL;";
                    alter.ExecuteNonQuery();
                }
            }

            using var index = connection.CreateCommand();
            index.Transaction = tx;
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_messages_thread_key ON messages(thread_key);";
            index.ExecuteNonQuery();
        }),

        // 版本 25：类型判定依据。列表/阅读区要能回答「为什么判成这一类」——
        // 存命中的词与位置（key:提单样本@12）或「继承会话类型」（thread:inherit），只存词不存正文。
        (25, "MailTypeBasis", static (connection, tx) =>
        {
            EnsureColumn(connection, tx, "messages", "mail_type_basis", "TEXT NOT NULL DEFAULT ''");
        }),

        // 版本 26：运输字段（箱型箱量 / 航线对 / 贸易条款 / 金额）。
        // 一封邮件一行；`lane_raw` 保留下原写法，供 `lane:` 搜索与回看。
        (26, "ShipmentFields", static (connection, tx) =>
        {
            using var create = connection.CreateCommand();
            create.Transaction = tx;
            create.CommandText = """
CREATE TABLE IF NOT EXISTS message_shipment_fields(
    email_id   INTEGER PRIMARY KEY,
    boxes      TEXT NOT NULL DEFAULT '',
    lane_pol   TEXT NOT NULL DEFAULT '',
    lane_pod   TEXT NOT NULL DEFAULT '',
    lane_raw   TEXT NOT NULL DEFAULT '',
    incoterm   TEXT NOT NULL DEFAULT '',
    amount     REAL,
    currency   TEXT NOT NULL DEFAULT '',
    basis      TEXT NOT NULL DEFAULT '',
    created_utc TEXT NOT NULL
);
""";
            create.ExecuteNonQuery();

            using var index = connection.CreateCommand();
            index.Transaction = tx;
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_message_shipment_fields_lane ON message_shipment_fields(lane_pol, lane_pod);";
            index.ExecuteNonQuery();
        }),

        // 版本 27：10 万封规模的性能索引。
        // ① 待补扫计数按 body_indexed 取子集；② 会话模式按 (thread_key, date_utc) 覆盖分组，
        //    避免每页都回表读 10 万行。
        (27, "PerfIndexes100k", static (connection, tx) =>
        {
            using (var scanIndex = connection.CreateCommand())
            {
                scanIndex.Transaction = tx;
                scanIndex.CommandText = "CREATE INDEX IF NOT EXISTS ix_message_ref_scan_body ON message_ref_scan(body_indexed);";
                scanIndex.ExecuteNonQuery();
            }

            using (var threadIndex = connection.CreateCommand())
            {
                threadIndex.Transaction = tx;
                threadIndex.CommandText = "CREATE INDEX IF NOT EXISTS ix_messages_thread_date ON messages(thread_key, date_utc DESC);";
                threadIndex.ExecuteNonQuery();
            }
        })
    };

    /// <summary>
    /// 一次性回填（meta 里打标记，只跑一次）：与收信时同一套 <see cref="BounceMailPolicy"/>。
    /// 这里只有发件人与主题可用，所以只走「退信类发件人 + 退信主题」双条件；
    /// 延迟通知（delay / 延迟）在策略里已被排除，不会误标。
    /// </summary>
    private static void BackfillBounceMarksOnce(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM meta WHERE key = @key;";
            check.Parameters.AddWithValue("@key", BounceBackfillKey);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;
        }

        using var tx = connection.BeginTransaction();
        BackfillBounceMarks(connection, tx);

        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "INSERT INTO meta(key, value) VALUES(@key, @value);";
            stamp.Parameters.AddWithValue("@key", BounceBackfillKey);
            stamp.Parameters.AddWithValue("@value", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            stamp.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 按「发件人 + 主题」扫一遍还没有退信标识的邮件并打标，返回标记数量。
    /// 13k 级邮件的一次性扫描在迁移事务里完成，耗时可接受。
    /// </summary>
    private static int BackfillBounceMarks(SqliteConnection connection, SqliteTransaction tx)
    {
        var matches = new List<long>();

        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id, from_name, from_email, subject FROM messages WHERE is_bounce = 0;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var fromName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var fromEmail = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var subject = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                if (BounceMailPolicy.IsBounce(fromEmail, fromName, subject)) matches.Add(id);
            }
        }

        foreach (var id in matches)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET is_bounce = 1 WHERE id = @id;";
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }

        return matches.Count;
    }

    private readonly SqliteConnectionFactory _factory;
    private readonly Action<string>? _trace;
    private readonly object _gate = new();
    private bool _initialized;

    /// <param name="trace">
    /// 可选的分段计时回调（启动诊断用）：每完成一步回调一次，带累计毫秒数。
    /// 测试里不传，保持零开销。
    /// </param>
    public DatabaseInitializer(SqliteConnectionFactory factory, Action<string>? trace = null)
    {
        _factory = factory;
        _trace = trace;
    }

    /// <summary>
    /// 读取库里的架构版本（毫秒级，只查 meta）：启动流程用它判断「要不要先弹升级进度窗口」。
    /// 库不存在或还没初始化时返回 0。
    /// </summary>
    public int PeekSchemaVersion()
    {
        using var connection = _factory.Create();
        return ReadSchemaVersion(connection);
    }

    /// <summary>
    /// 幂等初始化；多次调用仅首次执行 DDL 与迁移。
    /// <paramref name="progress"/> 用于升级进度窗口：老库升级时按迁移步骤逐步回报，
    /// 机械盘上重建索引可能持续几分钟，没有进度用户会以为程序挂了。
    /// </summary>
    public void Initialize(IProgress<DatabaseUpgradeProgress>? progress = null)
    {
        lock (_gate)
        {
            if (_initialized) return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string step) => _trace?.Invoke($"数据库 {step} {clock.ElapsedMilliseconds}ms");

            using var connection = _factory.Create();
            Mark("连接");

            // 必须在建表前读取版本：SchemaSql 只补缺失对象，不会补列。
            var currentVersion = ReadSchemaVersion(connection);
            Mark($"读版本(v{currentVersion})");

            var pending = Migrations.Where(m => m.Version > currentVersion).ToList();
            var totalSteps = pending.Count + 1;   // +1 = 补齐结构这一步
            var completed = 0;
            void Report(string detail) => progress?.Report(new DatabaseUpgradeProgress(++completed, totalSteps, detail));

            // 快路径：版本已是最新且核心对象都在 → 跳过整份 SchemaSql。
            // 老版本每次启动都重跑两百多条 CREATE ... IF NOT EXISTS，在慢盘 / 冷缓存
            // 的机器上光这一项就能吃掉几十秒，而结构其实早就齐了。
            if (currentVersion < SchemaVersion || !CoreSchemaPresent(connection))
            {
                Report("准备数据结构（补齐缺失的表与索引）");

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = SchemaSql;
                    command.ExecuteNonQuery();
                }

                Mark("建表索引");

                ApplyMigrations(connection, pending, Report);
                Mark("迁移");
            }
            else
            {
                Mark("结构已就绪跳过DDL");
            }

            ScheduleShipmentBackfill(connection);
            ScheduleMailTypeBackfill(connection);
            RepairLegacyInlineImages(connection);
            BackfillBounceMarksOnce(connection);
            Mark("一次性修复检查");

            _initialized = true;
        }
    }

    /// <summary>
    /// 核心对象是否齐全：防止有人手工删表 / 半途中断留下的残缺库被快路径放过。
    /// 只查 sqlite_master，代价是一次毫秒级查询。
    /// </summary>
    private static bool CoreSchemaPresent(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*) FROM sqlite_master
WHERE (type = 'table' AND name IN ('messages', 'folders', 'accounts', 'attachments', 'pending_operations'))
   OR (name = 'messages_fts');
""";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) >= 6;
    }

    /// <summary>读取已落库的架构版本；全新数据库（无 meta 表或未写入版本）返回 0。</summary>
    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';";
            if (Convert.ToInt64(exists.ExecuteScalar() ?? 0L) == 0) return 0;
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return int.TryParse(query.ExecuteScalar() as string, out var version) ? version : 0;
    }

    private static void ApplyMigrations(
        SqliteConnection connection,
        IReadOnlyList<(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Apply)> pending,
        Action<string> report)
    {
        if (pending.Count == 0) return;

        // 升级期间放松落盘：整批迁移放在一个事务里，最贵的是「加生成列 + 建索引」这种全表扫描。
        // WAL + synchronous=OFF 能省掉大量 fsync（机械盘上实测占大头）；进程中途被杀时该事务整体回滚，
        // 版本号没提交，下次启动从原版本重试，不会留半套结构。
        Execute(connection, "PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-131072;");
        try
        {
            using var tx = connection.BeginTransaction();

            foreach (var migration in pending)
            {
                report($"更新数据结构：{DescribeMigration(migration.Name)}");
                migration.Apply(connection, tx);

                using var mark = connection.CreateCommand();
                mark.Transaction = tx;
                mark.CommandText =
                    "INSERT INTO meta(key, value) VALUES('schema_version', @version) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                mark.Parameters.AddWithValue("@version", migration.Version.ToString(CultureInfo.InvariantCulture));
                mark.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            // 恢复连接默认值（工厂里的 ApplyPragmas 是 WAL + synchronous=NORMAL）
            Execute(connection, "PRAGMA synchronous=NORMAL; PRAGMA temp_store=DEFAULT; PRAGMA cache_size=-16384;");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>迁移步骤的中文说明（升级进度窗口展示用）；未登记的走英文名。</summary>
    private static string DescribeMigration(string name) => name switch
    {
        "PendingOperations" => "待发队列字段",
        "MailRules" => "过滤规则表",
        "TagsAndTodos" => "标签与待办表",
        "FreightReferences" => "业务单号表",
        "ShipmentCases" => "一票货案件表",
        "FollowUps" => "等待回复表",
        "MailTypeAndStage" => "邮件类型与阶段",
        "TagShortcut" => "标签快捷键",
        "TagAccountScope" => "标签按账户隔离",
        "AccountFetchPolicy" => "账户收信策略",
        "AccountPrimaryFlag" => "主账户标记",
        "StrangerSender" => "陌生发件人标记",
        "BounceMark" => "退信标记",
        "ThreadKeyIndex" => "会话归并索引（全表重建，最慢的一步）",
        _ => name
    };

    /// <summary>补列前先查 pragma_table_info，保证迁移对已手工修补过的旧库同样安全。</summary>
    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        string column,
        string type)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @name;";
            check.Parameters.AddWithValue("@name", column);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 修复旧版本在代码页未注册时以 Latin-1 形式落库的主题。仅处理含典型乱码标记、
    /// 且候选文本明显包含俄文或中文的记录，避免改写正常的西文标题。
    /// </summary>
    private static void RepairLegacySubjects(SqliteConnection connection, SqliteTransaction tx)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var updates = new List<(long Id, string Subject)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT id, subject FROM messages WHERE subject IS NOT NULL AND subject <> '';";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var original = reader.GetString(1);
                var repaired = TryRepairLegacySubject(original);
                if (!string.Equals(original, repaired, StringComparison.Ordinal)) updates.Add((id, repaired));
            }
        }

        foreach (var (id, subject) in updates)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET subject = @subject, fts_subject = @ftsSubject WHERE id = @id;";
            update.Parameters.AddWithValue("@subject", subject);
            update.Parameters.AddWithValue("@ftsSubject", FtsTextNormalizer.Normalize(subject));
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }
    }

    private static string TryRepairLegacySubject(string value)
    {
        // U+00BB/U+00D8/U+00B8 常见于 GBK 被当成 Latin-1；Ð/Ñ/Ã/Â 常见于 UTF-8 或 CP1251 的乱码。
        if (value.IndexOfAny(new[] { '»', 'Ø', '¸', '¼', 'Ð', 'Ñ', 'Ã', 'Â' }) < 0) return value;

        byte[] bytes;
        try
        {
            bytes = Encoding.Latin1.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            return value;
        }

        var gb18030 = Encoding.GetEncoding("GB18030").GetString(bytes);
        // »Ø¸´ / ·Å²Õ 一类标记几乎总是 GBK 被当作 Latin-1；优先恢复中文，
        // 不能仅以「西里尔字符数量」评分，否则会被 CP1251 的随机字符误导。
        if (HasGbkMojibakeMarker(value) && CountCjk(gb18030) >= 2) return gb18030;

        var candidates = new[]
        {
            Encoding.UTF8.GetString(bytes),
            Encoding.GetEncoding(1251).GetString(bytes),
            gb18030
        };

        var currentScore = ReadabilityScore(value);
        var best = candidates.OrderByDescending(ReadabilityScore).First();
        return ReadabilityScore(best) >= currentScore + 6 ? best : value;
    }

    /// <summary>v4 已写入的伪俄文可逆：按 CP1251 回转为原始字节，再按 GB18030 解码。</summary>
    private static void RepairMisclassifiedGbkSubjects(SqliteConnection connection, SqliteTransaction tx)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var repairs = new List<(long Id, string Subject)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT id, subject FROM messages WHERE subject IS NOT NULL AND instr(subject, '»') > 0;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var original = reader.GetString(1);
                if (!original.Any(character => character is >= '\u0400' and <= '\u052F')) continue;

                var bytes = Encoding.GetEncoding(1251).GetBytes(original);
                var repaired = Encoding.GetEncoding("GB18030").GetString(bytes);
                if (CountCjk(repaired) >= 2) repairs.Add((reader.GetInt64(0), repaired));
            }
        }

        foreach (var (id, subject) in repairs)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET subject = @subject, fts_subject = @ftsSubject WHERE id = @id;";
            update.Parameters.AddWithValue("@subject", subject);
            update.Parameters.AddWithValue("@ftsSubject", FtsTextNormalizer.Normalize(subject));
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }
    }

    private static bool HasGbkMojibakeMarker(string value)
        => value.IndexOfAny(new[] { '»', 'Ø', '¸', '¼', '¡', '¾', '·', 'É' }) >= 0;

    private static int CountCjk(string value) => value.Count(character => character is >= '\u4E00' and <= '\u9FFF');

    private static int ReadabilityScore(string value)
    {
        var score = 0;
        foreach (var character in value)
        {
            if (character is >= '\u0400' and <= '\u052F') score += 4; // Cyrillic
            else if (character is >= '\u4E00' and <= '\u9FFF') score += 3; // CJK unified ideographs
            else if (character == '\uFFFD' || char.IsControl(character)) score -= 8;
        }
        return score;
    }

    /// <summary>
    /// v15 的存量回填：已有邮件在 v14 时已建立扫描记录，不会被补扫队列重新捡起，
    /// 于是时效节点与一票货案件会一直是空的。这里清空一次扫描进度，
    /// 让后台补扫把全部邮件重抽一遍（同时补齐单号、时效节点与案件归属），仅执行一次。
    /// </summary>
    private static void ScheduleShipmentBackfill(SqliteConnection connection)
        => ResetScanProgressOnce(connection, ShipmentBackfillKey);

    /// <summary>v17 的存量回填：行业类型与案件阶段同样需要重抽一次历史邮件。</summary>
    private static void ScheduleMailTypeBackfill(SqliteConnection connection)
        => ResetScanProgressOnce(connection, MailTypeBackfillKey);

    /// <summary>按 meta 标记执行一次的「清空扫描进度」，让后台补扫重新处理全部邮件。</summary>
    private static void ResetScanProgressOnce(SqliteConnection connection, string key)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM meta WHERE key = @key;";
            check.Parameters.AddWithValue("@key", key);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;
        }

        using var tx = connection.BeginTransaction();

        using (var reset = connection.CreateCommand())
        {
            reset.Transaction = tx;
            reset.CommandText = "DELETE FROM message_ref_scan;";
            reset.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO meta(key, value) VALUES(@key, @value);";
            mark.Parameters.AddWithValue("@key", key);
            mark.Parameters.AddWithValue("@value", DateTimeOffset.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 旧版本只保存正文与普通附件，multipart/related 的内嵌图片从未落盘，
    /// 导致缓存正文中的 cid: 图片永久空白。把这类邮件标记为「正文未下载」，
    /// 用户下次打开时按需重新下载并按新逻辑保存（只执行一次）。
    /// </summary>
    private static void RepairLegacyInlineImages(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM meta WHERE key = @key;";
            check.Parameters.AddWithValue("@key", InlineImageRepairKey);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;
        }

        using var tx = connection.BeginTransaction();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET body_state = 0 WHERE body_state = 1 AND body_html LIKE '%cid:%';";
            update.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO meta(key, value) VALUES(@key, @value);";
            mark.Parameters.AddWithValue("@key", InlineImageRepairKey);
            mark.Parameters.AddWithValue("@value", DateTimeOffset.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// FTS5 采用「内容表 + 辅助列」方案：C# 侧写入 messages 前先把归一化文本存入
    /// fts_subject/fts_from/fts_to/fts_body 辅助列（中文逐字切分），触发器仅把这四列搬到
    /// messages_fts（非外部内容表，用 rowid=id 对齐），从而保证增删改时索引与主表严格同步、无脏数据。
    /// </summary>
    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS meta(
    key   TEXT PRIMARY KEY,
    value TEXT
);

CREATE TABLE IF NOT EXISTS accounts(
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT NOT NULL,
    email         TEXT NOT NULL,
    display_name  TEXT,
    username      TEXT,
    secret_enc    TEXT,
    auth_kind     INTEGER NOT NULL DEFAULT 0,
    imap_host     TEXT,
    imap_port     INTEGER NOT NULL DEFAULT 993,
    imap_secure   INTEGER NOT NULL DEFAULT 0,
    smtp_host     TEXT,
    smtp_port     INTEGER NOT NULL DEFAULT 465,
    smtp_secure   INTEGER NOT NULL DEFAULT 0,
    is_enabled    INTEGER NOT NULL DEFAULT 1,
    fetch_policy  INTEGER NOT NULL DEFAULT 0,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    color         TEXT,
    created_utc   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS folders(
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id     INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    full_name      TEXT NOT NULL,
    display_name   TEXT,
    kind           INTEGER NOT NULL DEFAULT 0,
    uid_validity   INTEGER NOT NULL DEFAULT 0,
    last_max_uid   INTEGER NOT NULL DEFAULT 0,
    last_sync_utc  TEXT,
    unread_count   INTEGER NOT NULL DEFAULT 0,
    total_count    INTEGER NOT NULL DEFAULT 0,
    is_selectable  INTEGER NOT NULL DEFAULT 1,
    UNIQUE(account_id, full_name)
);

CREATE TABLE IF NOT EXISTS messages(
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id     INTEGER NOT NULL,
    folder_id      INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    uid            INTEGER NOT NULL,
    message_id     TEXT,
    thread_id      TEXT,
    subject        TEXT,
    from_name      TEXT,
    from_email     TEXT,
    to_json        TEXT,
    cc_json        TEXT,
    bcc_json       TEXT,
    reply_to       TEXT,
    references_json TEXT,
    date_utc       TEXT NOT NULL,
    preview        TEXT,
    flags          INTEGER NOT NULL DEFAULT 0,
    size           INTEGER NOT NULL DEFAULT 0,
    has_attachment INTEGER NOT NULL DEFAULT 0,
    body_state     INTEGER NOT NULL DEFAULT 0,
    body_html      TEXT,
    body_text      TEXT,
    eml_path       TEXT,
    fts_subject    TEXT,
    fts_from       TEXT,
    fts_to         TEXT,
    fts_body       TEXT,
    -- 会话归并键：thread_id 为空的本机会话用 local:id 兜底；虚拟列不占空间。
    -- 索引 ix_messages_thread_key 只在迁移 v24 里建：升级路径上 SchemaSql 先于迁移执行，
    -- 老库此刻还没有这一列，在这里建索引会直接报 no such column。
    thread_key     TEXT GENERATED ALWAYS AS (COALESCE(NULLIF(thread_id, ''), 'local:' || id)) VIRTUAL,
    UNIQUE(folder_id, uid)
);

CREATE INDEX IF NOT EXISTS ix_messages_folder_date  ON messages(folder_id, date_utc DESC);
CREATE INDEX IF NOT EXISTS ix_messages_account_date ON messages(account_id, date_utc DESC);
CREATE INDEX IF NOT EXISTS ix_messages_thread       ON messages(thread_id);
CREATE INDEX IF NOT EXISTS ix_messages_unread       ON messages(folder_id, flags);
-- 历史清理按时间/大小扫描时避免全表排序
CREATE INDEX IF NOT EXISTS ix_messages_date         ON messages(date_utc DESC);
CREATE INDEX IF NOT EXISTS ix_messages_size         ON messages(size);

CREATE TABLE IF NOT EXISTS attachments(
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    file_name    TEXT,
    content_type TEXT,
    size         INTEGER NOT NULL DEFAULT 0,
    is_inline    INTEGER NOT NULL DEFAULT 0,
    local_path   TEXT
);

CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(message_id);

-- 清理任务：可追溯的审计记录（谁在何时、按什么条件清理了多少邮件）
CREATE TABLE IF NOT EXISTS cleanup_jobs(
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    mode            INTEGER NOT NULL DEFAULT 0,
    status          INTEGER NOT NULL DEFAULT 0,
    filter_json     TEXT NOT NULL DEFAULT '',
    total_count     INTEGER NOT NULL DEFAULT 0,
    processed_count INTEGER NOT NULL DEFAULT 0,
    total_bytes     INTEGER NOT NULL DEFAULT 0,
    freed_bytes     INTEGER NOT NULL DEFAULT 0,
    started_utc     TEXT NOT NULL,
    finished_utc    TEXT,
    note            TEXT
);

CREATE INDEX IF NOT EXISTS ix_cleanup_jobs_started ON cleanup_jobs(started_utc DESC);

-- 清理明细：保留完整邮件快照，撤销时按原 id 与附件路径恢复
CREATE TABLE IF NOT EXISTS cleanup_items(
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id           INTEGER NOT NULL REFERENCES cleanup_jobs(id) ON DELETE CASCADE,
    message_id       INTEGER NOT NULL,
    account_id       INTEGER NOT NULL,
    folder_id        INTEGER NOT NULL,
    folder_name      TEXT,
    subject          TEXT,
    from_email       TEXT,
    date_utc         TEXT,
    size             INTEGER NOT NULL DEFAULT 0,
    attachment_bytes INTEGER NOT NULL DEFAULT 0,
    payload_json     TEXT NOT NULL,
    eml_rel_path     TEXT,
    UNIQUE(job_id, message_id)
);

CREATE INDEX IF NOT EXISTS ix_cleanup_items_job ON cleanup_items(job_id);

CREATE TABLE IF NOT EXISTS contacts(
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    email          TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name           TEXT,
    last_used_utc  TEXT NOT NULL,
    use_count      INTEGER NOT NULL DEFAULT 0,
    is_whitelisted INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS outbox(
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id       INTEGER NOT NULL,
    to_json          TEXT,
    cc_json          TEXT,
    bcc_json         TEXT,
    subject          TEXT,
    body_html        TEXT,
    is_html          INTEGER NOT NULL DEFAULT 1,
    attachments_json TEXT,
    scheduled_utc    TEXT,
    state            INTEGER NOT NULL DEFAULT 0,
    last_error       TEXT,
    attempts         INTEGER NOT NULL DEFAULT 0,
    created_utc      TEXT NOT NULL,
    in_reply_to      TEXT,
    "references"     TEXT,
    next_attempt_utc TEXT,
    message_id       TEXT
);

CREATE TABLE IF NOT EXISTS notes(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT,
    content     TEXT,
    updated_utc TEXT NOT NULL
);

-- FTS5：非外部内容表，用 rowid=id 与主表对齐，email_id 冗余一列便于 JOIN。
CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
    subject,
    from_text,
    to_text,
    body,
    email_id UNINDEXED,
    tokenize='unicode61'
);

-- 主表新增/删除/更新时同步 FTS：文本已在 C# 侧归一化存入辅助列，触发器仅做搬运。
CREATE TRIGGER IF NOT EXISTS trg_messages_fts_ai AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, from_text, to_text, body, email_id)
    VALUES (new.id, COALESCE(new.fts_subject, ''), COALESCE(new.fts_from, ''),
            COALESCE(new.fts_to, ''), COALESCE(new.fts_body, ''), new.id);
END;

CREATE TRIGGER IF NOT EXISTS trg_messages_fts_ad AFTER DELETE ON messages BEGIN
    DELETE FROM messages_fts WHERE rowid = old.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_messages_fts_au AFTER UPDATE ON messages BEGIN
    DELETE FROM messages_fts WHERE rowid = old.id;
    INSERT INTO messages_fts(rowid, subject, from_text, to_text, body, email_id)
    VALUES (new.id, COALESCE(new.fts_subject, ''), COALESCE(new.fts_from, ''),
            COALESCE(new.fts_to, ''), COALESCE(new.fts_body, ''), new.id);
END;

-- 一致性兜底：清理孤立索引行，并为缺失的邮件补齐 FTS 行（幂等）。
DELETE FROM messages_fts WHERE rowid NOT IN (SELECT id FROM messages);
INSERT INTO messages_fts(rowid, subject, from_text, to_text, body, email_id)
SELECT id, COALESCE(fts_subject, ''), COALESCE(fts_from, ''), COALESCE(fts_to, ''),
       COALESCE(fts_body, ''), id
FROM messages
WHERE id NOT IN (SELECT rowid FROM messages_fts);
""" + PendingOperationSchemaSql;

    /// <summary>待同步操作队列（版本 3）：本地优先写操作的持久化意图与状态机。</summary>
    private const string PendingOperationSchemaSql = """
-- 待同步操作：Pending / Syncing / Succeeded / Failed
CREATE TABLE IF NOT EXISTS pending_operations(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    email_id    INTEGER NOT NULL DEFAULT 0,
    kind        INTEGER NOT NULL,
    state       INTEGER NOT NULL DEFAULT 0,
    flags       INTEGER NOT NULL DEFAULT 0,
    payload     TEXT,
    attempts    INTEGER NOT NULL DEFAULT 0,
    last_error  TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_pending_operations_state ON pending_operations(state);
""";

    /// <summary>邮件规则（版本 6）：条件与动作，命中统计用于回溯自动化效果。</summary>
    private const string MailRuleSchemaSql = """
CREATE TABLE IF NOT EXISTS mail_rules(
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT NOT NULL DEFAULT '',
    from_contains   TEXT NOT NULL DEFAULT '',
    subject_contains TEXT NOT NULL DEFAULT '',
    action          INTEGER NOT NULL DEFAULT 0,
    target_folder_id INTEGER,
    enabled         INTEGER NOT NULL DEFAULT 1,
    hit_count       INTEGER NOT NULL DEFAULT 0,
    last_hit_utc    TEXT
);

CREATE INDEX IF NOT EXISTS ix_mail_rules_enabled ON mail_rules(enabled);
""";

    /// <summary>标签与待办（版本 7）。</summary>
    private const string TagTodoSchemaSql = """
-- 标签按账户隔离；shortcut_key 是单键（1-9 / A-Z / F2-F12）。
-- 旧的 shortcut / shortcut_gesture 列保留但不再使用（v19 迁移会清空 gesture）。
CREATE TABLE IF NOT EXISTS mail_tags(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT NOT NULL,
    color      TEXT NOT NULL DEFAULT '#2563eb',
    shortcut   INTEGER,
    shortcut_gesture TEXT,
    account_id INTEGER,
    shortcut_key TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS message_tags(
    email_id    INTEGER NOT NULL,
    tag_id      INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    PRIMARY KEY(email_id, tag_id)
);

CREATE INDEX IF NOT EXISTS ix_message_tags_tag ON message_tags(tag_id);

CREATE TABLE IF NOT EXISTS mail_todos(
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    email_id      INTEGER NOT NULL,
    title         TEXT NOT NULL DEFAULT '',
    due_utc       TEXT,
    state         INTEGER NOT NULL DEFAULT 0,
    created_utc   TEXT NOT NULL,
    completed_utc TEXT
);

CREATE INDEX IF NOT EXISTS ix_mail_todos_state_due ON mail_todos(state, due_utc);
CREATE INDEX IF NOT EXISTS ix_mail_todos_email ON mail_todos(email_id);
""";

    /// <summary>
    /// 货代业务标识（版本 14）。<c>message_refs</c> 保存每封邮件抽出的单号，
    /// <c>message_ref_scan</c> 记录扫描是否已覆盖正文——邮件头先到、正文后到时需要重扫。
    /// </summary>
    private const string FreightReferenceSchemaSql = """
CREATE TABLE IF NOT EXISTS message_refs(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    email_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind       INTEGER NOT NULL,
    value      TEXT NOT NULL,
    normalized TEXT NOT NULL,
    source     INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_message_refs_unique
    ON message_refs(email_id, kind, normalized);
CREATE INDEX IF NOT EXISTS ix_message_refs_lookup
    ON message_refs(kind, normalized);

CREATE TABLE IF NOT EXISTS message_ref_scan(
    email_id     INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
    body_indexed INTEGER NOT NULL DEFAULT 0,
    scanned_utc  TEXT NOT NULL
);
""";

    /// <summary>
    /// 货代时效节点与一票货案件（版本 15）。
    /// <c>message_deadlines</c> 是看板的事实来源（每封邮件抽出的截关 / ETD / ETA）；
    /// <c>case_refs</c> 保证一个单号只归属一个案件，<c>case_messages</c> 是案件时间轴。
    /// </summary>
    private const string ShipmentCaseSchemaSql = """
CREATE TABLE IF NOT EXISTS message_deadlines(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    email_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind        INTEGER NOT NULL,
    due_utc     TEXT NOT NULL,
    raw_text    TEXT NOT NULL DEFAULT '',
    created_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_message_deadlines_unique
    ON message_deadlines(email_id, kind, due_utc);
CREATE INDEX IF NOT EXISTS ix_message_deadlines_due ON message_deadlines(due_utc);

CREATE TABLE IF NOT EXISTS shipment_cases(
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    primary_kind      INTEGER NOT NULL DEFAULT 0,
    primary_ref       TEXT NOT NULL,
    primary_value     TEXT NOT NULL DEFAULT '',
    status            INTEGER NOT NULL DEFAULT 0,
    created_utc       TEXT NOT NULL,
    last_activity_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_shipment_cases_primary
    ON shipment_cases(primary_kind, primary_ref);
CREATE INDEX IF NOT EXISTS ix_shipment_cases_activity
    ON shipment_cases(last_activity_utc DESC);

CREATE TABLE IF NOT EXISTS case_refs(
    case_id    INTEGER NOT NULL REFERENCES shipment_cases(id) ON DELETE CASCADE,
    kind       INTEGER NOT NULL,
    normalized TEXT NOT NULL,
    value      TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(case_id, kind, normalized)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_case_refs_alias ON case_refs(kind, normalized);
CREATE INDEX IF NOT EXISTS ix_case_refs_case ON case_refs(case_id);

CREATE TABLE IF NOT EXISTS case_messages(
    case_id    INTEGER NOT NULL REFERENCES shipment_cases(id) ON DELETE CASCADE,
    email_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    linked_utc TEXT NOT NULL,
    PRIMARY KEY(case_id, email_id)
);

CREATE INDEX IF NOT EXISTS ix_case_messages_email ON case_messages(email_id);

-- 邮件删除后（cascade 已清掉 case_messages），顺手清掉空案件与孤儿别名，
-- 避免「资料包已删、案件还挂在列表里」。
CREATE TRIGGER IF NOT EXISTS trg_shipment_cases_cleanup AFTER DELETE ON messages BEGIN
    DELETE FROM shipment_cases WHERE id NOT IN (SELECT DISTINCT case_id FROM case_messages);
    DELETE FROM case_refs WHERE case_id NOT IN (SELECT id FROM shipment_cases);
END;
""";

    /// <summary>
    /// 「等待回复」跟进（版本 16）。回复集合在检查时从 <c>messages</c> 实时推导，
    /// 这里只保存判定输入与提醒状态，避免回复表与邮件表双写不一致。
    /// </summary>
    private const string FollowUpSchemaSql = """
CREATE TABLE IF NOT EXISTS mail_followups(
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    email_id       INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    thread_id      TEXT NOT NULL DEFAULT '',
    subject        TEXT NOT NULL DEFAULT '',
    from_email     TEXT NOT NULL DEFAULT '',
    sent_utc       TEXT NOT NULL,
    recipients     TEXT NOT NULL DEFAULT '[]',
    require_all    INTEGER NOT NULL DEFAULT 1,
    min_replies    INTEGER NOT NULL DEFAULT 1,
    include_cc     INTEGER NOT NULL DEFAULT 0,
    next_check_utc TEXT NOT NULL,
    state          INTEGER NOT NULL DEFAULT 0,
    notified_utc   TEXT,
    note           TEXT NOT NULL DEFAULT '',
    created_utc    TEXT NOT NULL,
    updated_utc    TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_mail_followups_state_next
    ON mail_followups(state, next_check_utc);
CREATE INDEX IF NOT EXISTS ix_mail_followups_email
    ON mail_followups(email_id);
""";
}

