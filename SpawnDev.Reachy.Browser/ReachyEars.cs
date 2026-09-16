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

            // A COUNT IS NOT A WORKING MICROPHONE. A remote track can be present and carrying nothing:
            // `muted` is set BY THE BROWSER when no media is flowing from the peer, and `readyState`
            // tells "live" apart from "ended". Neither raises an error, and a capture built on such a
            // track simply never yields a frame - which reads as a broken capture pipeline rather than
            // as a track with no data in it. Print all three the moment the media arrives.
            var first = audio is { Length: > 0 } ? audio[0] : null;
            var trackInfo = first == null ? ""
                : $" [state={first.ReadyState} muted={first.Muted} enabled={first.Enabled} "
                + $"label='{first.Label}']";
            Log?.Invoke($"[reachy-ears] robot media arrived: {audio?.Length ?? 0} audio track(s){trackInfo}");

            if (HasAudio) AttachSink(stream);

            // `muted` on a remote track is not a user setting and cannot be cleared from this side - it
            // flips on its own when packets start arriving. Report the transition rather than sampling
            // once, because whether it ever unmutes is the whole question.
            if (first != null)
            {
                first.OnUnMute += () => Log?.Invoke("[reachy-ears] audio track UNMUTED - packets are flowing");
                first.OnMute += () => Log?.Invoke("[reachy-ears] audio track MUTED - packets stopped");
                first.OnEnded += () => Log?.Invoke("[reachy-ears] audio track ENDED");
            }
            if (HasAudio) StreamArrived?.Invoke(stream);
            else Log?.Invoke("[reachy-ears] no audio track - this robot is not carrying microphone audio");
        }
        catch (Exception ex)
        {
            // 🔴 An unhandled exception on a JS callback EXITS the WASM runtime; it does not fail a turn.
            Log?.Invoke($"[reachy-ears] failed to read the robot's media: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Give the remote audio track a renderer, or Chrome never decodes it.
    /// </summary>
    /// <remarks>
    /// 🔴 THE TRACK BEING LIVE IS NOT ENOUGH. MEASURED 2026-09-16: the robot's track reported
    /// <c>state=live</c> and then <c>unmuted</c> - which the browser only does once RTP is arriving - and a
    /// <c>MediaStreamTrackProcessor</c> built on it still sat on a read that never completed and never
    /// threw. Chrome does not run the decode pipeline for a remote audio track that nothing renders, so a
    /// processor alone is a consumer of something that is never produced. The symptom is the worst kind:
    /// no frames, no error, no end-of-stream - a capture that has "started" and delivers silence forever.
    ///
    /// The SDK's own <c>attachVideo</c> is what normally supplies this sink; an app that only wants the
    /// AUDIO has no reason to call it and no way to know it must. Hence an element of our own, owned and
    /// disposed here.
    ///
    /// ⚠️ MUTED, and off-screen. The element exists to make the browser decode, not to play the robot out
    /// of the computer's speakers - doing that would put the robot's microphone into the room it is
    /// listening to. Muted also keeps it inside the autoplay policy, so it works on a page that has had no
    /// user gesture yet; an unmuted element would have <c>play()</c> rejected there.
    /// </remarks>
    private void AttachSink(MediaStream stream)
    {
        try
        {
            var el = ReachyMiniJs.CreateAudioSink();
            el.Muted = true;
            el.SrcObject = stream;
            // play() explicitly rather than an autoplay attribute: the element is never in the document,
            // so nothing would trigger autoplay for it. Muted keeps this inside the autoplay policy.
            _ = el.Play();
            _sink = el;
            Log?.Invoke("[reachy-ears] attached a muted sink so the browser decodes the robot's audio");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[reachy-ears] could not attach an audio sink, capture will stay silent: "
                      + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private HTMLAudioElement? _sink;

    /// <summary>Stop listening. Required before the client is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Every += needs its -=, or the JS callback outlives this object and calls into a disposed one.
        try { _js.OnVideoTrack -= _handler; } catch { /* the client may already be gone */ }
        // The sink holds the stream; leaving it would keep the browser decoding a robot nobody is
        // listening to for as long as the page is open.
        try { if (_sink is { } sink) { sink.SrcObject = null; sink.Dispose(); } } catch { }
        _sink = null;
        Stream = null;
    }
}
