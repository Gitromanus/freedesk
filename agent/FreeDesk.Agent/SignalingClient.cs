using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreeDesk.Agent;

/// <summary>Клиент signaling API. Токен устройства подставляется в каждый запрос.</summary>
public sealed class SignalingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly AgentConfig _config;

    public SignalingClient(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.Server.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<RegisterResult> RegisterAsync(string name, string os, CancellationToken ct)
    {
        return await SendAsync<RegisterResult>(
            HttpMethod.Post, "api/v1/device/register", new { name, os }, ct);
    }

    public async Task PresenceAsync(CancellationToken ct)
    {
        await SendAsync<JsonElement>(HttpMethod.Post, "api/v1/presence", null, ct);
    }

    public async Task<IncomingResult> IncomingAsync(CancellationToken ct)
    {
        return await SendAsync<IncomingResult>(HttpMethod.Get, "api/v1/session/incoming", null, ct);
    }

    public async Task<RequestResult> RequestAsync(string targetId, CancellationToken ct)
    {
        return await SendAsync<RequestResult>(
            HttpMethod.Post, "api/v1/session/request", new { target_id = targetId }, ct);
    }

    public async Task<AnswerResult> AnswerAsync(string sessionId, string action, CancellationToken ct)
    {
        return await SendAsync<AnswerResult>(
            HttpMethod.Post, "api/v1/session/answer",
            new { session_id = sessionId, action }, ct);
    }

    public async Task SignalAsync(string sessionId, string type, object? payload, CancellationToken ct)
    {
        await SendAsync<JsonElement>(
            HttpMethod.Post, "api/v1/session/signal",
            new { session_id = sessionId, type, payload }, ct);
    }

    public async Task<ResolveResult> ResolveAsync(string publicId, CancellationToken ct)
    {
        return await SendAsync<ResolveResult>(HttpMethod.Get, $"api/v1/resolve/{publicId}", null, ct);
    }

    public async Task<PollResult> PollAsync(string sessionId, long since, CancellationToken ct)
    {
        return await SendAsync<PollResult>(
            HttpMethod.Get, $"api/v1/session/poll?session_id={sessionId}&since={since}", null, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);

        if (!string.IsNullOrEmpty(_config.DeviceToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeviceToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ApiException(
                "bad_response",
                $"сервер вернул не JSON (HTTP {(int)response.StatusCode}): {Truncate(text)}",
                (int)response.StatusCode);
        }

        using (document)
        {
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

            if (!response.IsSuccessStatusCode || !ok)
            {
                var code = root.TryGetProperty("error", out var e) ? e.GetString() ?? "error" : "http_error";
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? text : text;
                throw new ApiException(code, message, (int)response.StatusCode);
            }

            return root.Deserialize<T>(JsonOptions)
                   ?? throw new ApiException("bad_response", "пустой ответ сервера", 500);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";

    public void Dispose() => _http.Dispose();
}
