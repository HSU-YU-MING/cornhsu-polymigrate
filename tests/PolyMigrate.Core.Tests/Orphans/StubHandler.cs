using System.Net;

namespace PolyMigrate.Core.Tests.Orphans;

/// <summary>測試用 HTTP handler:依 URL 回應,並記錄所有請求(不打真站)。</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add($"{request.Method} {request.RequestUri}");
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public static HttpResponseMessage Html(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };
        response.Content.Headers.ContentType = new("text/html") { CharSet = "utf-8" };
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }
}
