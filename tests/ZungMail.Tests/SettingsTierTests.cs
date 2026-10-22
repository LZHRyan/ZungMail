using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 设置页档位下拉的取值：配置文件里的值不在候选档位时，界面要落到最接近的档位，
/// 不能出现 <c>SelectedIndex = -1</c> 的空白选项。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SettingsTierTests
{
    private static readonly int[] PageSizes = { 20, 50, 100, 200, 300, 500 };

    [Theory]
    [InlineData(20, 0)]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    [InlineData(500, 5)]
    public void 命中档位时返回原下标(int value, int expected)
        => Assert.Equal(expected, SettingsTier.NearestIndex(PageSizes, value));

    [Theory]
    [InlineData(10, 0)]    // 比最小档还小 → 最小档
    [InlineData(30, 0)]    // 离 20 比离 50 近
    [InlineData(40, 1)]    // 离 50 更近
    [InlineData(150, 2)]   // 离 100 更近
    [InlineData(260, 4)]   // 离 300 更近
    [InlineData(9999, 5)]  // 比最大档还大 → 最大档
    public void 非档位值落到最接近的档位(int value, int expected)
        => Assert.Equal(expected, SettingsTier.NearestIndex(PageSizes, value));

    [Fact]
    public void 空档位返回负一()
        => Assert.Equal(-1, SettingsTier.NearestIndex(Array.Empty<int>(), 100));

    [Fact]
    public void 单档位始终返回零()
        => Assert.Equal(0, SettingsTier.NearestIndex(new[] { 100 }, 9999));
}
