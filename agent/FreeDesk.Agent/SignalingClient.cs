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
        // имя уезжает с каждым heartbeat: переименование в настройках применяется само
        await SendAsync<JsonElement>(
            HttpMethod.Post, "api/v1/presence", new { name = _config.Name }, ct);
    }

    public async Task<IncomingResult> IncomingAsync(CancellationToken ct, int waitSeconds = 0)
    {
        var wait = Math.Clamp(waitSeconds, 0, 15);
        return await SendAsync<IncomingResult>(
            HttpMethod.Get, $"api/v1/session/incoming?wait={wait}", null, ct);
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

    /// <summary>
    /// Опрос сигналов сессии. waitSeconds включает long-poll на сервере:
    /// без него клиент долбит сервер 2 раза в секунду и жжёт CPU хостинга.
    /// </summary>
    public async Task<PollResult> PollAsync(string sessionId, long since, CancellationToken ct, int waitSeconds = 0)
    {
        var wait = Math.Clamp(waitSeconds, 0, 20);
        return await SendAsync<PollResult>(
            HttpMethod.Get, $"api/v1/session/poll?session_id={sessionId}&since={since}&wait={wait}", null, ct);
    }

    // ─────────── резервный канал (когда NAT не пропустил P2P) ───────────

    /// <summary>Хост отдаёт кадр в резервный канал (не дольше 10 с: застрявший
    /// запрос не должен держать остановку стримера).</summary>
    public async Task RelayFrameAsync(string sessionId, byte[] jpeg, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v1/relay/frame?session_id={sessionId}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeviceToken);
        request.Content = new ByteArrayContent(jpeg);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using var response = await _http.SendAsync(request, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"relay frame: {(int) response.StatusCode}");
        }
    }

    /// <summary>
    /// Кадр с проверкой новизны в одном запросе: сервер держит запрос (long-poll),
    /// пока не появится кадр новее since. Возвращает (jpeg|null, новый seq).
    /// </summary>
    public async Task<(byte[]? Jpeg, int Seq)> RelayFrameAsync(
        string sessionId, int since, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v1/relay/image?session_id={sessionId}&since={since}&wait=5");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeviceToken);

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"relay image: {(int) response.StatusCode}");
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? "";

        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return (null, since);
        }

        var jpeg = await response.Content.ReadAsByteArrayAsync(ct);
        var seq = since;

        if (response.Headers.TryGetValues("X-Frame-Seq", out var values))
        {
            int.TryParse(values.FirstOrDefault(), out seq);
        }

        return (jpeg, seq);
    }

    /// <summary>Зритель отправляет событие ввода.</summary>
    public async Task RelayControlPushAsync(string sessionId, string json, CancellationToken ct)
    {
        await SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/relay/control?session_id={sessionId}",
            new { @event = JsonSerializer.Deserialize<JsonElement>(json) }, ct);
    }

    /// <summary>Хост забирает события ввода после байтового смещения (long-poll).</summary>
    public async Task<RelayControl> RelayControlPullAsync(
        string sessionId, long offset, CancellationToken ct, int waitSeconds = 5) =>
        await SendAsync<RelayControl>(HttpMethod.Get,
            $"api/v1/relay/control?session_id={sessionId}&offset={offset}&wait={Math.Clamp(waitSeconds, 0, 5)}", null, ct);

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
