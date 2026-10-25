using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 摘要模型仓库：下载写盘、SHA256 校验、失败清理、删除与手动放置。用本机 TCP 小服务发字节，
/// 不依赖外网与真实模型。
/// </summary>
public sealed class SummaryModelStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JsonSettingsStore _settings;

    public SummaryModelStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-model-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _settings = new JsonSettingsStore(paths);
        _settings.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private SummaryModelStore CreateStore(string? url, string? sha)
    {
        _settings.Current.MailSummaryModelUrl = url ?? "";
        _settings.Current.MailSummaryModelSha256 = sha ?? "";
        return new SummaryModelStore(_settings, _root, NullLogger<SummaryModelStore>.Instance);
    }

    [Fact]
    public async Task 下载成功并校验通过()
    {
        var payload = BuildPayload(2 * 1024 * 1024);
        using var server = new SingleFileHttpServer(payload);
        var store = CreateStore(server.Url, Convert.ToHexString(SHA256.HashData(payload)));

        var progress = new List<string>();
        var status = await store.DownloadAsync(new Progress<MailSummaryProgress>(update => progress.Add(update.Stage)));

        Assert.Equal(SummaryModelState.Ready, status.State);
        Assert.True(File.Exists(store.ModelFilePath));
        Assert.Equal(payload.Length, new FileInfo(store.ModelFilePath).Length);
        Assert.Contains(progress, stage => stage.Contains("下载"));
    }

    [Fact]
    public async Task 校验不通过时删除文件并报错()
    {
        var payload = BuildPayload(1024 * 1024);
        using var server = new SingleFileHttpServer(payload);
        var store = CreateStore(server.Url, new string('a', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync());

        Assert.False(File.Exists(store.ModelFilePath));
        Assert.Equal(SummaryModelState.Missing, store.GetStatus().State);
    }

    [Fact]
    public void 删除模型后回到未下载()
    {
        var payload = BuildPayload(2 * 1024 * 1024);
        // 默认内置了官方模型的 SHA256：测试文件要用自己的哈希，否则会被判成「校验不匹配」
        var store = CreateStore(null, Convert.ToHexString(SHA256.HashData(payload)));
        Directory.CreateDirectory(Path.GetDirectoryName(store.ModelFilePath)!);
        File.WriteAllBytes(store.ModelFilePath, payload);

        Assert.Equal(SummaryModelState.Ready, store.GetStatus().State);

        store.Delete();

        Assert.Equal(SummaryModelState.Missing, store.GetStatus().State);
    }

    [Fact]
    public void 手动放置文件后重新检测即为就绪()
    {
        var payload = BuildPayload(2 * 1024 * 1024);
        var store = CreateStore(null, Convert.ToHexString(SHA256.HashData(payload)));
        Directory.CreateDirectory(Path.GetDirectoryName(store.ModelFilePath)!);
        File.WriteAllBytes(store.ModelFilePath, payload);

        var status = store.Recheck();

        Assert.Equal(SummaryModelState.Ready, status.State);
        Assert.True(status.SizeBytes > 0);
    }

    [Fact]
    public async Task 地址不合法时提示()
    {
        // 默认已内置官方下载地址，这里显式给一个非 http 的地址验证校验逻辑
        var store = CreateStore("ftp://example.com/model.gguf", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DownloadAsync());

        Assert.Contains("必须是 http/https", ex.Message);
    }

    [Fact]
    public void 过小的文件按损坏处理()
    {
        var store = CreateStore(null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ModelFilePath)!);
        File.WriteAllBytes(store.ModelFilePath, new byte[16]);

        Assert.Equal(SummaryModelState.Invalid, store.GetStatus().State);
    }

    private static byte[] BuildPayload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    /// <summary>只回 200 的小 HTTP 服务（避免 HttpListener 的 urlacl 限制）。</summary>
    private sealed class SingleFileHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public SingleFileHttpServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/model.gguf";
            _loop = Task.Run(() => AcceptAsync(_cts.Token));
        }

        public string Url { get; }

        private async Task AcceptAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    using var stream = client.GetStream();

                    var buffer = new byte[4096];
                    var header = new StringBuilder();
                    while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read <= 0) break;
                        header.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var head = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                               $"Content-Length: {_payload.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
                    await stream.WriteAsync(_payload, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 正常停止
            }
            catch (ObjectDisposedException)
            {
                // 监听器已关闭
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _cts.Dispose();
        }
    }
}
