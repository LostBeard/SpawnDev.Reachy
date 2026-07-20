using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.Reachy;

/// <summary>
/// Client for the GStreamer <c>webrtcsink</c> signalling server the Reachy Mini
/// runs on port 8443.
/// </summary>
/// <remarks>
/// This is the transport that makes a roaming robot work: the daemon's
/// upload-a-WAV-then-play endpoints require a round trip per utterance and give
/// no microphone access at all, while this session carries Opus audio in BOTH
/// directions plus H.264 video. The microphone side is the 4-mic array with
/// hardware echo cancellation in the XVF3800, so the robot does not transcribe
/// its own speech.
///
/// Protocol verified live against daemon v1.9.0: plain <c>ws://</c> (no TLS),
/// server sends <c>welcome</c> unprompted, and the robot advertises itself as a
/// producer with <c>meta.name == "reachymini"</c>.
/// </remarks>
public sealed class GstSignallingClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;

    /// <summary>Our own peer id, assigned by the server in the welcome message.</summary>
    public string? PeerId { get; private set; }

    /// <summary>Session id once <see cref="StartSessionAsync"/> succeeds.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Raised when the remote peer sends an SDP offer.</summary>
    public event Action<string>? OnSdpOffer;

    /// <summary>Raised for each remote ICE candidate: (candidate, sdpMLineIndex).</summary>
    public event Action<string, int>? OnIceCandidate;

    public GstSignallingClient(string hostOrIp, int port = 8443)
    {
        _uri = new Uri($"ws://{hostOrIp}:{port}");
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_uri, ct);

        // The server greets us before we say anything.
        var welcome = await ReceiveJsonAsync(ct)
            ?? throw new InvalidOperationException("No welcome message from signalling server.");
        PeerId = welcome.RootElement.TryGetProperty("peerId", out var p) ? p.GetString() : null;

        await SendAsync("""{"type":"setPeerStatus","roles":["listener"]}""", ct);
        _ = await ReceiveJsonAsync(ct);   // peerStatusChanged
    }

    /// <summary>Lists available producers. The robot's meta name is "reachymini".</summary>
    public async Task<List<(string Id, string? Name)>> ListProducersAsync(CancellationToken ct = default)
    {
        await SendAsync("""{"type":"list"}""", ct);

        var result = new List<(string, string?)>();
        // The list reply may be preceded by unrelated notifications.
        for (var i = 0; i < 5; i++)
        {
            using var doc = await ReceiveJsonAsync(ct);
            if (doc is null) break;
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.GetString() != "list") continue;
            if (!doc.RootElement.TryGetProperty("producers", out var ps)) continue;

            foreach (var prod in ps.EnumerateArray())
            {
                var id = prod.GetProperty("id").GetString();
                string? name = null;
                if (prod.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("name", out var n)) name = n.GetString();
                if (id is not null) result.Add((id, name));
            }
            break;
        }
        return result;
    }

    private readonly TaskCompletionSource<string> _sessionStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Requests a session with a producer and waits for confirmation. The producer
    /// then sends its SDP offer via <see cref="OnSdpOffer"/>.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="ReceiveLoopAsync"/> to already be running. A WebSocket
    /// supports exactly ONE concurrent reader - having this method read its own
    /// reply while the pump was also reading raced them, and whichever won ate the
    /// other's message.
    /// </remarks>
    public async Task StartSessionAsync(string producerPeerId, CancellationToken ct = default)
    {
        await SendAsync($$"""{"type":"startSession","peerId":"{{producerPeerId}}"}""", ct);
        SessionId = await _sessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>
    /// The single reader for this socket. Dispatches every incoming message.
    /// Must be running before <see cref="StartSessionAsync"/> is called.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            JsonDocument? doc;
            try { doc = await ReceiveJsonAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (doc is null) break;

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;

                switch (t.GetString())
                {
                    case "sessionStarted":
                        if (root.TryGetProperty("sessionId", out var sid))
                            _sessionStarted.TrySetResult(sid.GetString() ?? "");
                        break;

                    case "peer":
                        // The offer can arrive before or after sessionStarted, so
                        // latch the session id from whichever shows up first.
                        if (SessionId is null && root.TryGetProperty("sessionId", out var psid))
                        {
                            SessionId = psid.GetString();
                            _sessionStarted.TrySetResult(SessionId ?? "");
                        }

                        if (root.TryGetProperty("sdp", out var sdp)
                            && sdp.TryGetProperty("sdp", out var sdpText))
                        {
                            OnSdpOffer?.Invoke(sdpText.GetString() ?? "");
                        }
                        else if (root.TryGetProperty("ice", out var ice)
                                 && ice.TryGetProperty("candidate", out var cand))
                        {
                            var line = ice.TryGetProperty("sdpMLineIndex", out var idx) ? idx.GetInt32() : 0;
                            OnIceCandidate?.Invoke(cand.GetString() ?? "", line);
                        }
                        break;

                    case "endSession":
                        return;
                }
            }
        }
    }

    public Task SendAnswerAsync(string sdp, CancellationToken ct = default)
    {
        var msg = new SignalPeerSdp("peer", SessionId!, new SdpPayload("answer", sdp));
        return SendAsync(JsonSerializer.Serialize(msg, SignalJson.Default.SignalPeerSdp), ct);
    }

    public Task SendIceAsync(string candidate, int sdpMLineIndex, CancellationToken ct = default)
    {
        var msg = new SignalPeerIce("peer", SessionId!, new IcePayload(candidate, sdpMLineIndex));
        return SendAsync(JsonSerializer.Serialize(msg, SignalJson.Default.SignalPeerIce), ct);
    }

    private Task SendAsync(string json, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private async Task<JsonDocument?> ReceiveJsonAsync(CancellationToken ct)
    {
        // Messages can exceed one frame - an SDP offer is several KB.
        var buffer = new ArrayBufferWriter<byte>();
        var chunk = new byte[16384];
        while (true)
        {
            var r = await _ws.ReceiveAsync(chunk, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            buffer.Write(chunk.AsSpan(0, r.Count));
            if (r.EndOfMessage) break;
        }
        if (buffer.WrittenCount == 0) return null;
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* closing a dead socket is not interesting */ }
        _ws.Dispose();
    }
}

internal record SdpPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sdp")] string Sdp);

internal record IcePayload(
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("sdpMLineIndex")] int SdpMLineIndex);

internal record SignalPeerSdp(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sdp")] SdpPayload Sdp);

internal record SignalPeerIce(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("ice")] IcePayload Ice);

[JsonSerializable(typeof(SignalPeerSdp))]
[JsonSerializable(typeof(SignalPeerIce))]
internal partial class SignalJson : JsonSerializerContext;
