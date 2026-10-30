using System.Text.Json.Serialization;

namespace FreeDesk.Agent;

public sealed record PeerInfo(
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os")] string Os);

public sealed record RegisterResult(
    [property: JsonPropertyName("device_id")] int DeviceId,
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("device_token")] string DeviceToken,
    [property: JsonPropertyName("heartbeat_seconds")] int HeartbeatSeconds);

public sealed record IncomingItem(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("from")] PeerInfo From,
    [property: JsonPropertyName("created_at")] string CreatedAt);

public sealed record IncomingResult(
    [property: JsonPropertyName("incoming")] List<IncomingItem> Incoming);

public sealed record SessionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("role")] string Role);

public sealed record SignalMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] System.Text.Json.JsonElement Payload);

public sealed record PollResult(
    [property: JsonPropertyName("session")] SessionInfo Session,
    [property: JsonPropertyName("messages")] List<SignalMessage> Messages,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("server_time")] long ServerTime);

public sealed record RequestResult(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("target")] PeerInfo Target,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record AnswerResult(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("state")] string State);

public sealed record ResolveResult(
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("idle_seconds")] int? IdleSeconds,
    [property: JsonPropertyName("self")] bool Self);

public sealed class ApiException : Exception
{
    public string Code { get; }
    public int Status { get; }

    public ApiException(string code, string message, int status) : base($"{code}: {message}")
    {
        Code = code;
        Status = status;
    }
}

/// <summary>Порция событий ввода из резервного канала.</summary>
public sealed record RelayControl(
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("events")] List<string>? Events);
