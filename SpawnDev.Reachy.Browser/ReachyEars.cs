using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.Reachy.Browser;

/// <summary>
/// The robot's own microphone array, as a <see cref="MediaStream"/> the host can capture from.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 SUBSCRIBE BEFORE CONNECTING. The SDK emits <c>videoTrack</c> ONCE, during the connect handshake,
/// and offers no way to ask for the current stream afterwards - the only accessor it has is the
/// <c>&lt;video&gt;</c> element <c>attachVideo</c> happens to have assigned. A listener attached after
/// <c>autoConnect</c> resolves therefore hears nothing, on a robot that is working perfectly, with no
/// error anywhere. Construct this against the client and THEN connect.
/// </para>
/// <para>
/// ⚠️ IT DOES NOT DECODE ANYTHING. Turning the stream into PCM is the host's job, because the host is
/// what already owns a capture pipeline - <c>SpawnDev.ILGPU.ML</c>'s <c>MediaStreamCapture</c> has
/// <c>StartFromAudioStreamAsync(MediaStream, …)</c> for exactly this, and it was written because
/// <c>getUserMedia</c> "is right for a browser demo and useless for a robot". Pulling that dependency in
/// here would tie this package to an inference engine to hand over a handle it already has.
/// </para>
/// <para>
/// ⚠️ The four-mic array has hardware echo cancellation in its XVF3800, so the robot does not transcribe
/// its own speech. That is a property to rely on, not to re-solve in software.
/// </para>
/// </remarks>
public sealed class ReachyEars : IDisposable
{
    private readonly ReachyMiniJs _js;
    private readonly Action<CustomEvent<ReachyMediaDetail>> _handler;
    private bool _disposed;

    /// <summary>Listen for <paramref name="js"/>'s robot media. Do this BEFORE connecting.</summary>
    public ReachyEars(ReachyMiniJs js)
    {
        _js = js;
        _handler = OnTrack;
        _js.OnVideoTrack += _handler;
    }

    /// <summary>The robot's live media - camera and microphones - or null before the track arrives.</summary>
    public MediaStream? Stream { get; private set; }

    /// <summary>True once the robot's media has arrived and carries at least one audio track.</summary>
    public bool HasAudio { get; private set; }

    /// <summary>Raised when the robot's media arrives. Fires on the stream that <see cref="Stream"/> returns.</summary>
    public event Action<MediaStream>? StreamArrived;

    /// <summary>Diagnostic log, matching the speaker's <c>[reachy-…]</c> convention.</summary>
    public event Action<string>? Log;

    private void OnTrack(CustomEvent<ReachyMediaDetail> e)
    {
        try
        {
            using var detail = e.Detail;
            var stream = detail?.Stream;
            if (stream == null)
            {
                Log?.Invoke("[reachy-ears] videoTrack carried no stream");
                return;
            }

            // ⚠️ The event is named videoTrack but the stream is BOTH directions of media. Count the audio
            // tracks rather than assuming: a robot with no microphone fires the same event, and the
            // difference has to be a fact rather than a hope.
            var audio = stream.GetAudioTracks();
            HasAudio = audio is { Length: > 0 };
            Stream = stream;
            Log?.Invoke($"[reachy-ears] robot media arrived: {audio?.Length ?? 0} audio track(s)");
            if (HasAudio) StreamArrived?.Invoke(stream);
            else Log?.Invoke("[reachy-ears] no audio track - this robot is not carrying microphone audio");
        }
        catch (Exception ex)
        {
            // 🔴 An unhandled exception on a JS callback EXITS the WASM runtime; it does not fail a turn.
            Log?.Invoke($"[reachy-ears] failed to read the robot's media: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Stop listening. Required before the client is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Every += needs its -=, or the JS callback outlives this object and calls into a disposed one.
        try { _js.OnVideoTrack -= _handler; } catch { /* the client may already be gone */ }
        Stream = null;
    }
}
