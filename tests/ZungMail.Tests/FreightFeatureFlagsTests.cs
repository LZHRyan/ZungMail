using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>货代行业功能的开关语义：总开关一键全关，子项各自独立，缺失设置按全开处理。</summary>
[Trait("Category", "Unit")]
public sealed class FreightFeatureFlagsTests
{
    [Fact]
    public void 默认设置_四项全开()
    {
        var flags = FreightFeatureFlags.From(new AppSettings());

        Assert.Equal(FreightFeatureFlags.All, flags);
        Assert.True(flags.AnyRecognition);
    }

    [Fact]
    public void 总开关关闭_子项即使勾选也视为关闭()
    {
        var settings = new AppSettings
        {
            FreightFeaturesEnabled = false,
            FreightReferencesEnabled = true,
            FreightMailTypesEnabled = true,
            FreightDeadlinesEnabled = true,
            FreightFollowUpsEnabled = true,
        };

        var flags = FreightFeatureFlags.From(settings);

        Assert.Equal(FreightFeatureFlags.None, flags);
        Assert.False(flags.AnyRecognition);
    }

    [Fact]
    public void 子项独立生效_只关时效不影响单号()
    {
        var settings = new AppSettings { FreightDeadlinesEnabled = false };

        var flags = FreightFeatureFlags.From(settings);

        Assert.True(flags.References);
        Assert.True(flags.MailTypes);
        Assert.False(flags.Deadlines);
        Assert.True(flags.FollowUps);
        Assert.True(flags.AnyRecognition);
    }

    [Fact]
    public void 只开等待回复_不需要入库抽取()
    {
        var settings = new AppSettings
        {
            FreightReferencesEnabled = false,
            FreightMailTypesEnabled = false,
            FreightDeadlinesEnabled = false,
        };

        var flags = FreightFeatureFlags.From(settings);

        Assert.False(flags.AnyRecognition);
        Assert.True(flags.FollowUps);
    }

    [Fact]
    public void 设置缺失时按全开处理()
    {
        Assert.Equal(FreightFeatureFlags.All, FreightFeatureFlags.From(null));
    }
}
