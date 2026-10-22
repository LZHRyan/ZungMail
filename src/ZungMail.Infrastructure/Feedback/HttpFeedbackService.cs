using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.Update;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Feedback;

/// <summary>
/// 反馈的 HTTP 客户端：按 <see cref="UpdateRoutes"/> 的约定 POST 到升级服务。
/// 组装内容交给 <see cref="FeedbackPackageBuilder"/>，网络失败统一转成
/// <see cref="FeedbackSubmitResult"/>，调用方不需要 try/catch。
/// </summary>
public sealed class HttpFeedbackService : IFeedbackService, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    private readonly FeedbackPackageBuilder _builder;
    private readonly ISettingsStore _settings;
    private readonly UpdateAddressResolver _addressResolver;
    private readonly IApplicationVersion _version;
    private readonly ILogger<HttpFeedbackService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;

    public HttpFeedbackService(
        FeedbackPackageBuilder builder,
        ISettingsStore settings,
        UpdateAddressResolver addressResolver,
        IApplicationVersion version,
        ILogger<HttpFeedbackService> logger,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _builder = builder;
        _settings = settings;
        _addressResolver = addressResolver;
        _version = version;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        if (_ownsHttpClient)
        {
            _http.Timeout = _timeout;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ZungMail/{_version.Version} (Windows; update-protocol/{UpdateRoutes.ProtocolVersion})");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public FeedbackLogInfo InspectLog() => _builder.InspectLog();

    public Task<FeedbackRequest> BuildRequestAsync(FeedbackDraft draft, CancellationToken ct = default)
        => _builder.BuildRequestAsync(draft, ct);

    public async Task<FeedbackSubmitResult> SubmitAsync(FeedbackRequest request, CancellationToken ct = default)
    {
        var resolution = _addressResolver.Resolve(_settings.Current);
        if (!resolution.Success || resolution.Address is null)
        {
            // 没有地址是「未配置」；写了但解析失败是「地址不合法」——界面提示不同
            var status = resolution.Source == UpdateAddressSource.Missing
                ? FeedbackSubmitStatus.NotConfigured
                : FeedbackSubmitStatus.InvalidAddress;
            return FeedbackSubmitResult.Fail(status, resolution.Message);
        }

        var address = resolution.Address;
        var url = UpdateRoutes.BuildFeedbackUrl(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(request, UpdateJson.Options),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.PostAsync(url, content, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                FeedbackResponse? payload;
                await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                {
                    payload = await JsonSerializer
                        .DeserializeAsync<FeedbackResponse>(stream, UpdateJson.Options, timeout.Token)
                        .ConfigureAwait(false);
                }

                var id = string.IsNullOrWhiteSpace(payload?.Id) ? "" : payload!.Id;
                if (id.Length == 0)
                {
                    return FeedbackSubmitResult.Fail(
                        FeedbackSubmitStatus.ServerError,
                        "升级服务返回的反馈编号为空，无法确认是否已收到。");
                }

                _logger.LogInformation("反馈已提交：{Id}（附加日志 {HasLog}）", id, request.Log is not null);
                return FeedbackSubmitResult.Ok(id, $"反馈已提交，编号 {id}");
            }

            var detail = await ReadErrorAsync(response, timeout.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => FeedbackSubmitResult.Fail(
                    FeedbackSubmitStatus.RateLimited,
                    "提交过于频繁，请稍后再试。" + detail),
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => FeedbackSubmitResult.Fail(
                        FeedbackSubmitStatus.Rejected,
                        "升级服务没有接受这条反馈" + detail),
                _ => FeedbackSubmitResult.Fail(
                    FeedbackSubmitStatus.ServerError,
                    $"升级服务返回 HTTP {(int)response.StatusCode}{detail}")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FeedbackSubmitResult.Fail(
                FeedbackSubmitStatus.Unreachable,
                $"连接升级服务超时（{_timeout.TotalSeconds:0} 秒）：{UpdateAddress.ToDisplay(address)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "提交反馈失败：{Address}", url);
            return FeedbackSubmitResult.Fail(
                FeedbackSubmitStatus.Unreachable,
                $"无法连接升级服务（{UpdateAddress.ToDisplay(address)}）：{Shorten(ex.Message)}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "反馈响应无法解析：{Address}", url);
            return FeedbackSubmitResult.Fail(
                FeedbackSubmitStatus.ServerError,
                "升级服务返回的内容不是有效的反馈响应（地址可能指向了其它服务）。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提交反馈异常：{Address}", url);
            return FeedbackSubmitResult.Fail(
                FeedbackSubmitStatus.ServerError,
                "提交反馈失败：" + Shorten(ex.Message));
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return "";
            if (text.Length > 400) text = text[..400];

            try
            {
                var payload = JsonSerializer.Deserialize<UpdateErrorResponse>(text, UpdateJson.Options);
                if (!string.IsNullOrWhiteSpace(payload?.Error)) return "：" + payload.Error;
            }
            catch (JsonException)
            {
                // 不是 JSON 就按纯文本截断
            }

            var line = text.Split('\n')[0].Trim();
            return line.Length == 0 ? "" : "：" + line;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Shorten(string message)
        => message.Length <= 400 ? message : message[..400] + "…";
}
