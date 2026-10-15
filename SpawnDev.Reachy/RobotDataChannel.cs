using System.Text;
using System.Text.Json;
using SpawnDev.RTC;

namespace SpawnDev.Reachy;

/// <summary>
/// The robot's control channel over the WebRTC session (the data channel the robot opens, which Pollen's JS SDK calls <c>_dc</c>), and the audio upload / play commands it carries.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the REST upload (<c>POST /api/media/sounds/upload</c>) is slow on a real robot - MEASURED 2026-10-15 with curl, 50 to 55 KB/s for 190 to 290 KB files (the daemon's
/// multipart handler on the Pi; the same Wi-Fi link downloads at 250 KB/s and pings in 3 ms), so a 6 s sentence waited 4 to 6 s before it could play. Pollen's own JS SDK (and the SpawnDev.AI demo on
/// Hugging Face Spaces, which sounds fine) uploads the clip over THIS channel instead and then asks the daemon to play it: base64 chunks of 12 KB as JSON, no multipart parsing.
/// The daemon plays it with the same <c>play_sound</c> as the REST path, so the sound is identical to an uploaded WAV; only the transfer is different.
/// </para>
/// <para>
/// The wire protocol is the daemon's (<c>reachy_mini/io/protocol.py</c>, <c>daemon/backend/abstract.py</c>), read from its source: <c>upload_audio_start</c> {upload_id, total_chunks, encoding:
/// "wav-base64", description}, <c>upload_audio_chunk</c> {upload_id, chunk_index, chunk}, in order, <c>upload_audio_finish</c>; then <c>play_uploaded_audio</c> {upload_id}, answered by a broadcast
/// {type: "play_uploaded_audio", upload_id, started: true} (or an <c>error</c> string); <c>cancel_audio</c> {upload_id} stops it. The encoding prefix is the file extension on the robot
/// ("flac-base64", "ogg-base64"...), so a compressed container is possible when size matters.
/// </para>
/// </remarks>
public sealed class RobotDataChannel
{
    /// <summary>Characters of base64 per chunk message (the SDK's 12 KB).</summary>
    public const int ChunkChars = 12 * 1024;

    private const long HighWater = 1024 * 1024, LowWater = 512 * 1024;

    private readonly IRTCDataChannel _channel;
    private readonly List<(Func<JsonElement, bool> Match, TaskCompletionSource<JsonElement> Result)> _waiters = new();
    private readonly object _lock = new();

    public RobotDataChannel(IRTCDataChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.OnStringMessage += OnMessage;
    }

    /// <summary>Whether the channel is open.</summary>
    public bool IsOpen => string.Equals(_channel.ReadyState, "open", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every message the robot sends (state broadcasts, command replies). Handlers must not block.</summary>
    public event Action<JsonElement>? Message;

    private void OnMessage(string text)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return; }
        List<TaskCompletionSource<JsonElement>> done = new();
        lock (_lock)
            for (var i = _waiters.Count - 1; i >= 0; i--)
                if (_waiters[i].Match(message)) { done.Add(_waiters[i].Result); _waiters.RemoveAt(i); }
        foreach (var d in done) d.TrySetResult(message);
        Message?.Invoke(message);
    }

    private bool Send(object command)
    {
        if (!IsOpen) return false;
        _channel.Send(JsonSerializer.Serialize(command));
        return true;
    }

    /// <summary>
    /// Uploads a clip to the robot over the channel and returns its upload id (play it with <see cref="PlayUploadedAudioAsync"/>). Chunks go out in order with flow control, so a long clip
    /// never piles more than a megabyte into the channel's buffer.
    /// </summary>
    /// <param name="audio">The file's bytes (a WAV by default).</param>
    /// <param name="encoding">"wav-base64" (the file extension before the dash decides how the robot stores and plays it).</param>
    public async Task<string> UploadAudioAsync(byte[] audio, string encoding = "wav-base64", string description = "audio", CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("the robot's data channel is not open");
        var id = "u" + Guid.NewGuid().ToString("N")[..11];
        var base64 = Convert.ToBase64String(audio);
        var chunks = Math.Max(1, (base64.Length + ChunkChars - 1) / ChunkChars);
        Send(new { type = "upload_audio_start", upload_id = id, total_chunks = chunks, encoding, description });
        for (var i = 0; i < chunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_channel.BufferedAmount > HighWater)
                while (_channel.BufferedAmount > LowWater)
                {
                    if (!IsOpen) throw new InvalidOperationException("the data channel closed mid-upload");
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
            var start = i * ChunkChars;
            if (!Send(new { type = "upload_audio_chunk", upload_id = id, chunk_index = i, chunk = base64.Substring(start, Math.Min(ChunkChars, base64.Length - start)) }))
                throw new InvalidOperationException("the data channel closed mid-upload");
        }
        Send(new { type = "upload_audio_finish", upload_id = id });
        return id;
    }

    /// <summary>Asks the robot to play an uploaded clip and waits for it to say it started (or to refuse: <see cref="InvalidOperationException"/>).</summary>
    public async Task PlayUploadedAudioAsync(string uploadId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var started = WaitFor(m => m.TryGetProperty("type", out var t) && t.GetString() == "play_uploaded_audio"
            && m.TryGetProperty("upload_id", out var u) && u.GetString() == uploadId
            && (m.TryGetProperty("started", out var s) && s.ValueKind == JsonValueKind.True || m.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String));
        if (!Send(new { type = "play_uploaded_audio", upload_id = uploadId })) throw new InvalidOperationException("the robot's data channel is not open");
        JsonElement reply;
        try { reply = await started.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
        finally { lock (_lock) _waiters.RemoveAll(w => ReferenceEquals(w.Result.Task, started)); }
        if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException("the robot would not play it: " + error.GetString());
    }

    /// <summary>Stops an uploaded clip that is playing (a stale id does nothing).</summary>
    public bool CancelAudio(string uploadId) => Send(new { type = "cancel_audio", upload_id = uploadId });

    private Task<JsonElement> WaitFor(Func<JsonElement, bool> match)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _waiters.Add((match, tcs));
        return tcs.Task;
    }
}
