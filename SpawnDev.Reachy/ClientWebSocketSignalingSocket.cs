using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace SpawnDev.Reachy;

/// <summary>
/// Default <see cref="ISignalingSocket"/> backed by a real <see cref="ClientWebSocket"/>. Used wherever the
/// scope can open <c>ws://</c> directly: the desktop / standalone path (the Rose Windows app), and - inside
/// a browser - an extension's BACKGROUND service worker, which unlike an https page is not mixed-content
/// blocked (verified 2026-08-18: the worker connects to <c>ws://reachy-mini.local:8443</c> and receives the
/// signalling server's <c>welcome</c>, while the same call from the page fails immediately).
/// </summary>
public sealed class ClientWebSocketSignalingSocket : ISignalingSocket
{
    private readonly ClientWebSocket _ws = new();

    /// <summary>Creates an unconnected socket.</summary>
    public ClientWebSocketSignalingSocket()
    {
        // The robot's signalling server is plain ws:// (no TLS), but leaving the callback permissive keeps
        // a wss:// deployment working too without a cert store. Under browser WASM there is no cert
        // plumbing to configure - ClientWebSocket is a thin wrapper over the JS WebSocket, whose TLS is the
        // browser's - and the option setters are not supported there, so skip it.
        if (!OperatingSystem.IsBrowser())
            _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }

    /// <inheritdoc />
    public bool IsOpen => _ws.State == WebSocketState.Open;

    /// <inheritdoc />
    public Task ConnectAsync(Uri uri, CancellationToken ct = default) => _ws.ConnectAsync(uri, ct);

    /// <inheritdoc />
    public Task SendTextAsync(string text, CancellationToken ct = default) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken ct = default)
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
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken ct = default)
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        }
        catch { /* closing a dead socket is not interesting */ }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }
}
