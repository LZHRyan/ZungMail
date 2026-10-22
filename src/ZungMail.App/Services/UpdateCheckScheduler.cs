using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 启动后自动检测新版本：等设置加载完，再按「升级」设置里的延迟执行一次。
/// 失败只写日志与状态，不弹窗——自动检测不能打扰正在写信/读信的用户。
/// </summary>
public sealed class UpdateCheckScheduler : BackgroundService
{
    private readonly ISettingsStore _settings;
    private readonly UpdateCoordinator _coordinator;
    private readonly ILogger<UpdateCheckScheduler> _logger;

    public UpdateCheckScheduler(
        ISettingsStore settings,
        UpdateCoordinator coordinator,
        ILogger<UpdateCheckScheduler> logger)
    {
        _settings = settings;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 UpdateCheck 进入");

        // 宿主先启动、设置后加载；留几秒余量再读配置，避免拿到默认值
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_settings.Current.UpdateAutoCheckEnabled)
        {
            _logger.LogInformation("自动升级检测已关闭，跳过启动检测");
            return;
        }

        var minutes = AppSettings.EffectiveUpdateDelayMinutes(_settings.Current.UpdateAutoCheckDelayMinutes);
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 等待期间用户可能在设置里关掉了自动检测
        if (!_settings.Current.UpdateAutoCheckEnabled)
        {
            _logger.LogInformation("自动升级检测已在等待期间关闭");
            return;
        }

        try
        {
            var result = await _coordinator.CheckAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "启动后自动升级检测完成：{Status}（{Message}）", result.Status, result.Message);
        }
        catch (OperationCanceledException)
        {
            // 客户端退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动后自动升级检测失败");
        }
    }
}
