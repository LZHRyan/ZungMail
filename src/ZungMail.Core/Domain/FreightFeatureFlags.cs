namespace ZungMail.Core.Domain;

/// <summary>
/// 货代行业功能的生效开关：设置页里是「总开关 + 4 个子项」，
/// 总开关关闭时四个子项一律视为关闭（子项本身的值保留，重新打开总开关即恢复）。
/// </summary>
public readonly record struct FreightFeatureFlags(
    bool References,
    bool MailTypes,
    bool Deadlines,
    bool FollowUps)
{
    /// <summary>四项全开（默认值，也是设置缺失时的行为）。</summary>
    public static FreightFeatureFlags All => new(true, true, true, true);

    /// <summary>四项全关。</summary>
    public static FreightFeatureFlags None => new(false, false, false, false);

    /// <summary>是否还有任何需要在入库时抽取的能力（业务编号 / 邮件类型 / 时效节点）。</summary>
    public bool AnyRecognition => References || MailTypes || Deadlines;

    public static FreightFeatureFlags From(AppSettings? settings)
    {
        if (settings is null) return All;
        if (!settings.FreightFeaturesEnabled) return None;

        return new FreightFeatureFlags(
            settings.FreightReferencesEnabled,
            settings.FreightMailTypesEnabled,
            settings.FreightDeadlinesEnabled,
            settings.FreightFollowUpsEnabled);
    }
}
