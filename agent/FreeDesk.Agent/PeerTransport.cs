using System.Net;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace FreeDesk.Agent;

/// <summary>
/// Транспорт между агентами. Видео идёт по RTP как MJPEG: SCTP-канал SIPSorcery
/// ограничен ~300 КБ/с, а RTP лимита не имеет. Data channel остаётся для управления.
/// </summary>
public interface IPeerTransport : IAsyncDisposable
{
    /// <summary>Локальный ICE-кандидат, который надо отправить второй стороне.</summary>
    event Action<object>? LocalSignal;

    /// <summary>Готовый JPEG-кадр от удалённой стороны.</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Сообщение управления (ввод) от удалённой стороны.</summary>
    event Action<string>? ControlReceived;

    event Action? Connected;
    event Action<string>? Closed;

    Task<object> CreateOfferAsync(CancellationToken ct);
    Task<object> CreateAnswerAsync(JsonElement offer, CancellationToken ct);
    Task ApplyAnswerAsync(JsonElement answer, CancellationToken ct);
    Task AddRemoteIceAsync(JsonElement candidate, CancellationToken ct);

    /// <summary>Отправить JPEG-кадр как MJPEG/RTP.</summary>
    void SendFrame(byte[] jpeg, uint durationRtpUnits);

    void SendControl(string json);

    /// <summary>Открыт ли data channel (можно слать управление и буфер обмена).</summary>
    bool IsConnected { get; }

    /// <summary>Сколько байт ещё не отправлено в data channel.</summary>
    long BufferedBytes { get; }
}

public sealed class WebRtcPeer : IPeerTransport
{
    public const int JpegPayloadType = 26;
    public const int JpegClockRate = 90000;

    private readonly RTCPeerConnection _pc;
    private readonly bool _offerer;
    private readonly List<RTCIceCandidateInit> _pendingCandidates = new();
    private RTCDataChannel? _channel;
    private bool _remoteDescriptionSet;
    private bool _connected;
    private bool _disposed;

    public event Action<object>? LocalSignal;
    public event Action<byte[]>? FrameReceived;
    public event Action<string>? ControlReceived;
    public event Action? Connected;
    public event Action<string>? Closed;

    public WebRtcPeer(bool offerer)
    {
        _offerer = offerer;

        var configuration = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
                new() { urls = "stun:stun1.l.google.com:19302" },
            },
        };

        _pc = new RTCPeerConnection(configuration);

        // Обе стороны объявляют JPEG-трек: экран отдаёт агент, но m-line должна быть
        // в offer, поэтому трек добавляет и зритель.
        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.JPEG, JpegPayloadType, JpegClockRate, null),
            MediaStreamStatusEnum.SendRecv);

        _pc.addTrack(videoTrack);

        _pc.OnVideoFrameReceived += (_, _, frame, _) =>
        {
            if (frame is { Length: > 0 })
            {
                FrameReceived?.Invoke(frame);
            }
        };

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrEmpty(candidate.candidate))
            {
                return;
            }

            LocalSignal?.Invoke(new Dictionary<string, object?>
            {
                ["type"] = "ice",
                ["candidate"] = candidate.candidate,
                ["sdpMid"] = candidate.sdpMid,
                ["sdpMLineIndex"] = candidate.sdpMLineIndex,
            });
        };

        _pc.ondatachannel += channel =>
        {
            Log.Info($"входящий data channel: {channel.label}, state={channel.readyState}");
            AttachChannel(channel);
        };

        _pc.onconnectionstatechange += state =>
        {
            Log.Info($"webrtc: {state}");

            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                Closed?.Invoke(state.ToString());
            }
        };
    }

    private void AttachChannel(RTCDataChannel channel)
    {
        _channel = channel;

        channel.onopen += () =>
        {
            Log.Info("data channel открыт");
            RaiseConnected();
        };

        channel.onclose += () => Closed?.Invoke("data channel закрыт");

        channel.onmessage += (_, protocol, data) =>
        {
            if (data is null || data.Length == 0)
            {
                return;
            }

            if (protocol == DataChannelPayloadProtocols.WebRTC_String)
            {
                ControlReceived?.Invoke(Encoding.UTF8.GetString(data));
            }
        };

        // У отвечающей стороны канал может быть уже открыт к этому моменту.
        if (channel.readyState == RTCDataChannelState.open)
        {
            Log.Info("data channel уже открыт");
            RaiseConnected();
        }
    }

    private void RaiseConnected()
    {
        if (_connected)
        {
            return;
        }

        _connected = true;
        Connected?.Invoke();
    }

    public async Task<object> CreateOfferAsync(CancellationToken ct)
    {
        if (_offerer && _channel is null)
        {
            AttachChannel(await _pc.createDataChannel("freedesk"));
        }

        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);

        return new Dictionary<string, object?> { ["type"] = "offer", ["sdp"] = offer.sdp };
    }

    public async Task<object> CreateAnswerAsync(JsonElement offer, CancellationToken ct)
    {
        var description = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offer.GetProperty("sdp").GetString(),
        };

        var result = _pc.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription: {result}");
        }

        _remoteDescriptionSet = true;
        FlushCandidates();

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        return new Dictionary<string, object?> { ["type"] = "answer", ["sdp"] = answer.sdp };
    }

    public Task ApplyAnswerAsync(JsonElement answer, CancellationToken ct)
    {
        var description = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.GetProperty("sdp").GetString(),
        };

        var result = _pc.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription(answer): {result}");
        }

        _remoteDescriptionSet = true;
        FlushCandidates();

        return Task.CompletedTask;
    }

    public Task AddRemoteIceAsync(JsonElement candidate, CancellationToken ct)
    {
        var init = new RTCIceCandidateInit
        {
            candidate = candidate.TryGetProperty("candidate", out var value) ? value.GetString() : null,
            sdpMid = candidate.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : "0",
            sdpMLineIndex = candidate.TryGetProperty("sdpMLineIndex", out var index)
                ? (ushort)index.GetInt32()
                : (ushort)0,
        };

        if (!_remoteDescriptionSet)
        {
            _pendingCandidates.Add(init);
            return Task.CompletedTask;
        }

        _pc.addIceCandidate(init);
        return Task.CompletedTask;
    }

    private void FlushCandidates()
    {
        foreach (var candidate in _pendingCandidates)
        {
            _pc.addIceCandidate(candidate);
        }

        _pendingCandidates.Clear();
    }

    public void SendFrame(byte[] jpeg, uint durationRtpUnits)
    {
        if (_channel is null || _channel.readyState != RTCDataChannelState.open)
        {
            return;
        }

        try
        {
            _pc.SendVideo(durationRtpUnits, jpeg);
        }
        catch (Exception ex)
        {
            Log.Warn($"кадр не отправлен: {ex.Message}");
        }
    }

    public void SendControl(string json)
    {
        var channel = _channel;
        if (channel is not null && channel.readyState == RTCDataChannelState.open)
        {
            channel.send(json);
        }
    }

    public long BufferedBytes => (long)(_channel?.bufferedAmount ?? 0);

    public bool IsConnected => _channel is not null && _channel.readyState == RTCDataChannelState.open;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _channel?.close();
        _pc.close();
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
