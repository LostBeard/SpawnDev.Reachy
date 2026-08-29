namespace SpawnDev.Reachy;

/// <summary>
/// A text WebSocket the <see cref="GstSignallingClient"/> talks the GStreamer webrtcsink signalling
/// protocol over. Abstracted so the transport can vary by host:
/// <list type="bullet">
/// <item>Desktop / standalone: a real <see cref="System.Net.WebSockets.ClientWebSocket"/>
/// (<see cref="ClientWebSocketSignalingSocket"/>).</item>
/// <item>Browser extension (Gemineachy): the content script cannot open an insecure <c>ws://</c> from an
/// https page (mixed-content blocked), so it injects a socket that relays frames through the extension's
/// background service worker, which owns the real <c>ws://</c> connection.</item>
/// </list>
/// The contract is deliberately message-oriented (one text frame in / one text frame out) - the signalling
/// protocol is line-of-JSON, not a byte stream, so callers never see partial frames.
/// </summary>
public interface ISignalingSocket : IAsyncDisposable
{
    /// <summary>Opens the connection to the signalling server.</summary>
    Task ConnectAsync(Uri uri, CancellationToken ct = default);

    /// <summary>Sends one text message (a complete JSON object).</summary>
    Task SendTextAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Receives the next complete text message, reassembling frames if the transport splits them. Returns
    /// <c>null</c> when the socket has closed and no more messages will arrive.
    /// </summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct = default);

    /// <summary>True while the socket is open and usable.</summary>
    bool IsOpen { get; }

    /// <summary>Closes the connection.</summary>
    Task CloseAsync(CancellationToken ct = default);
}
