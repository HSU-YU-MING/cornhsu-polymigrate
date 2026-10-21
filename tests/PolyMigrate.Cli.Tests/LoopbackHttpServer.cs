using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// 綁在 127.0.0.1 隨機埠的極簡 HTTP/1.1 伺服器,讓 probe-orphans / fetch-orphans 能走完整條 CLI 路徑
/// (CLI 自己建 HttpClient,沒有注入點)。不經過 DNS、不出本機;支援 keep-alive,
/// 一年 730 個 HEAD 探測在同一條連線上跑完。
/// </summary>
public sealed class LoopbackHttpServer : IAsyncDisposable
{
    /// <summary>一個回應:狀態碼、Content-Type(null = 不送)、內容。</summary>
    public sealed record Response(int Status, string? ContentType = null, byte[]? Body = null)
    {
        public static Response Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

        public static Response NotFound { get; } = new(404);
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string, Response> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    /// <param name="respond">(HTTP 方法, 路徑) → 回應。在伺服器執行緒上呼叫。</param>
    public LoopbackHttpServer(Func<string, string, Response> respond)
    {
        _respond = respond;
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _acceptLoop = AcceptLoop();
    }

    /// <summary>不含結尾斜線,可直接當 config 的 site.base_url。</summary>
    public string BaseUrl { get; }

    /// <summary>收到的請求,格式「方法 路徑」(依到達順序)。</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    private async Task AcceptLoop()
    {
        var connections = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                connections.Add(Serve(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        await Task.WhenAll(connections);
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                while (!_stop.IsCancellationRequested)
                {
                    var requestLine = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        return;   // 對方關了連線
                    }
                    // 讀掉標頭(這些請求都沒有 body)
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                    {
                    }
                    var parts = requestLine.Split(' ');
                    var (method, path) = (parts[0], parts[1]);
                    Requests.Enqueue($"{method} {path}");

                    var response = _respond(method, path);
                    var body = response.Body ?? [];
                    var head = new StringBuilder()
                        .Append($"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Status")}\r\n")
                        .Append($"Content-Length: {body.Length}\r\n");
                    if (response.ContentType is { } type)
                    {
                        head.Append($"Content-Type: {type}\r\n");
                    }
                    head.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token);
                    if (method != "HEAD")
                    {
                        await stream.WriteAsync(body, _stop.Token);
                    }
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // 用戶端中斷或伺服器關閉
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        _stop.Dispose();
    }
}
