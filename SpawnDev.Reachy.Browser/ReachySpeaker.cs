using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.Reachy.Browser;

/// <summary>
/// Plays synthesised speech out of the robot's OWN speaker, so a character with a body sounds like it is
/// in the room rather than coming from the computer.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE DAEMON DOES NOT TRANSCODE, AND DOES NOT VALIDATE. Audio must be <b>16 kHz mono 16-bit PCM
/// WAV</b>; it plays whatever it is handed, so a wrong rate, header or sample scaling is silence or
/// wrong-speed audio and never an error. The conversion is therefore done here rather than left to
/// callers to remember.
/// <para>
/// ✅ The encoder below is VERIFIED ON HARDWARE, 2026-09-16: a 1 s 440 Hz tone built by
/// <see cref="EncodeWav16"/> played from a real Reachy Mini's speaker at the right pitch and length,
/// confirmed by ear (<c>SpawnDev.Reachy.Rose --test-speaker</c>). That matters because no software on
/// this path can report the failure - the daemon accepted the upload and the play call identically when
/// the format would have been wrong.
/// </para>
/// </para>
/// <para>
/// 🔴 RESAMPLING IS DONE BY THE BROWSER, on purpose. Speech synthesis here runs at 24 kHz and the robot
/// wants 16 kHz, and naive decimation has already cost this project once: a <c>Resample()</c> with no
/// anti-aliasing filter folded everything from 8-24 kHz back onto the speech, and the result was not
/// obviously broken audio - it was audio whose formants were polluted, so the recogniser returned fluent,
/// confident, completely unrelated text. <see cref="OfflineAudioContext"/> band-limits properly, is
/// native code, and means this library needs no DSP of its own and no dependency on the ML stack to get
/// it right.
/// </para>
/// <para>
/// ⚠️ Playback is daemon-side (<c>uploadAudio</c> then <c>playUploadedAudio</c>) rather than streamed over
/// the WebRTC audio track, because the daemon then plays on its own clock. That is what keeps speech and
/// motion from drifting apart over a wireless link.
/// </para>
/// </remarks>
public sealed class ReachySpeaker
{
    private readonly ReachyMiniJs _js;

    /// <summary>The rate the daemon requires. Not configurable - it is the daemon's, not ours.</summary>
    public const int RobotSampleRate = 16000;

    /// <summary>Speak through <paramref name="js"/>'s robot.</summary>
    public ReachySpeaker(ReachyMiniJs js) => _js = js;

    /// <summary>
    /// Play start/end timeline, for proving clips do not overlap. Wire it to a log; do not leave it null
    /// on a path anyone is going to listen to.
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// Send one clip to the robot and wait for it to finish playing.
    /// </summary>
    /// <param name="samples">Mono float PCM, -1..1.</param>
    /// <param name="sampleRate">The rate <paramref name="samples"/> are at.</param>
    /// <returns>The clip's length in seconds, matching the player this replaces.</returns>
    /// <remarks>
    /// ⚠️ There is no <c>finished</c> event for standalone audio, so the wait is the clip's own duration
    /// and then a cancel. Returning early would let the next chunk start on top of this one - the same
    /// defect as fire-and-forget <c>play_sound</c>, which made sentence-at-a-time speech interrupt ITSELF
    /// a word or two in.
    /// </remarks>
    public async Task<double> PlayAsync(float[] samples, int sampleRate, CancellationToken ct = default)
    {
        if (samples.Length == 0) return 0;

        var wav = await ToRobotWavAsync(samples, sampleRate).ConfigureAwait(false);
        var seconds = wav.Seconds;

        using (var blob = new Blob(new[] { wav.Bytes }, new BlobOptions { Type = "audio/wav" }))
        {
            var uploadId = await _js.UploadAudioAsync(blob).ConfigureAwait(false);
            await _js.PlayUploadedAudioAsync(uploadId).ConfigureAwait(false);
        }

        // 🔴 START AND END ARE LOGGED so that clips not overlapping is verifiable from the TIMELINE and
        // not only by ear. The daemon's play call queues and returns, so a bug here does not throw - it
        // makes a reply interrupt ITSELF a word or two in, and that was heard by a person before there
        // was any instrument that could show it. Two lines are the instrument.
        var startedAt = DateTime.UtcNow;
        Log?.Invoke($"[reachy-speak] play start  {seconds:F2}s  {wav.Bytes.Length:N0} B @{RobotSampleRate} Hz");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-clip: stop the robot talking rather than leaving it to finish into whatever
            // comes next.
            _js.CancelAudio();
            Log?.Invoke($"[reachy-speak] play CANCELLED after "
                + $"{(DateTime.UtcNow - startedAt).TotalSeconds:F2}s of {seconds:F2}s");
            throw;
        }
        _js.CancelAudio();
        Log?.Invoke($"[reachy-speak] play end    {(DateTime.UtcNow - startedAt).TotalSeconds:F2}s elapsed");
        return seconds;
    }

    /// <summary>
    /// Stop the robot speaking immediately, for barge-in.
    /// </summary>
    /// <remarks>
    /// Both halves matter: <c>cancelAudio</c> drops the uploaded clip, and <c>clearIncomingAudio</c> drops
    /// anything already queued for the speaker. Cancelling only the first leaves queued audio to play on
    /// after the user has started talking.
    /// </remarks>
    public void Stop()
    {
        _js.CancelAudio();
        _js.ClearIncomingAudio();
    }

    /// <summary>
    /// Convert arbitrary-rate mono float PCM into the 16 kHz mono 16-bit WAV the daemon accepts.
    /// </summary>
    internal static async Task<(byte[] Bytes, double Seconds)> ToRobotWavAsync(float[] samples, int sampleRate)
    {
        var pcm = sampleRate == RobotSampleRate
            ? samples
            : await ResampleAsync(samples, sampleRate, RobotSampleRate).ConfigureAwait(false);
        return (EncodeWav16(pcm, RobotSampleRate), (double)pcm.Length / RobotSampleRate);
    }

    /// <summary>
    /// Resample with the browser's own audio engine, which band-limits before decimating.
    /// </summary>
    private static async Task<float[]> ResampleAsync(float[] samples, int srcRate, int dstRate)
    {
        var outLength = (long)Math.Ceiling(samples.Length * (double)dstRate / srcRate);
        if (outLength <= 0) return System.Array.Empty<float>();

        // The context renders AT the destination rate; the source buffer is declared at the SOURCE rate,
        // and the rate conversion is what the graph does between them.
        using var ctx = new OfflineAudioContext(1, outLength, dstRate);
        using var source = ctx.CreateBuffer(1, samples.Length, srcRate);
        using (var channel = new Float32Array(samples)) source.CopyToChannel(channel, 0);

        using var node = ctx.CreateBufferSource();
        node.Buffer = source;
        using (var destination = ctx.Destination) node.Connect(destination);
        node.Start();

        using var rendered = await ctx.StartRendering().ConfigureAwait(false);
        using var outChannel = rendered.GetChannelData(0);
        return outChannel.ToArray();
    }

    /// <summary>
    /// A canonical 16-bit PCM WAV. Small and explicit on purpose - the daemon rejects nothing, it just
    /// plays whatever it is handed, so a malformed header is silence rather than an error.
    /// </summary>
    internal static byte[] EncodeWav16(float[] samples, int sampleRate)
    {
        const int channels = 1, bitsPerSample = 16;
        var dataBytes = samples.Length * 2;
        var bytes = new byte[44 + dataBytes];
        var w = new BinaryWriter(new MemoryStream(bytes));

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                                             // PCM header size
        w.Write((short)1);                                       // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bitsPerSample / 8);      // byte rate
        w.Write((short)(channels * bitsPerSample / 8));          // block align
        w.Write((short)bitsPerSample);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);

        foreach (var s in samples)
        {
            // Clamp before scaling: a sample past 1.0 wraps to the opposite extreme as a 16-bit integer,
            // which is a loud click rather than the clipping anyone would expect.
            var clamped = s > 1f ? 1f : s < -1f ? -1f : s;
            w.Write((short)(clamped * short.MaxValue));
        }
        w.Flush();
        return bytes;
    }
}
