using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Turns text into speech in a character's voice and plays it through the robot.
/// </summary>
/// <remarks>
/// Audio comes out of ROSE, not the PC. That is the whole illusion - a voice from
/// a speaker on the desk is a computer talking, a voice from the robot is the robot
/// talking. Everything here routes through the daemon's sound endpoints.
/// </remarks>
public sealed class RoseVoice : IDisposable
{
    private readonly KokoroWavSynthesizer _synth;
    private readonly ReachyMiniClient _rose;
    private readonly Dictionary<string, KokoroVoice> _voices = [];

    /// <summary>
    /// Peak target after normalisation. Just under full scale, leaving a little
    /// room so the robot's own DAC does not clip on inter-sample peaks.
    /// </summary>
    private const float PeakTarget = 0.95f;

    /// <summary>
    /// Compression ratio applied above <see cref="CompressorThreshold"/>. Raising
    /// average level is the ONLY loudness lever we have left: the daemon volume
    /// reads 100, both ALSA PCM controls sit at 0.00 dB, and the XVF3800 exposes
    /// no speaker output gain. The ceiling is fixed, so we raise the floor.
    /// </summary>
    private const float CompressionRatio = 3.0f;
    private const float CompressorThreshold = 0.25f;

    public RoseVoice(ReachyMiniClient rose, string? modelPath = null)
    {
        _rose = rose;
        _synth = new KokoroWavSynthesizer(modelPath ?? ResolveModelPath());
    }

    /// <summary>
    /// Locates kokoro.onnx, downloading it via KokoroTTS if absent. The library
    /// drops it in the process working directory rather than a package cache, so
    /// the path has to be discovered rather than assumed.
    /// </summary>
    private static string ResolveModelPath()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(dir, "kokoro.onnx");
            if (File.Exists(candidate)) return candidate;
        }

        // Not present - this call downloads it (~310MB) and writes it to cwd.
        KokoroTTS.LoadModel();

        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(dir, "kokoro.onnx");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "kokoro.onnx not found after KokoroTTS.LoadModel(). Pass modelPath explicitly.");
    }

    private KokoroVoice GetVoice(string name)
    {
        if (_voices.TryGetValue(name, out var v)) return v;
        v = KokoroVoiceManager.GetVoice(name);
        _voices[name] = v;
        return v;
    }

    /// <summary>
    /// When false, skips loudness processing. Only useful for A/B comparison -
    /// leave it on in normal use, since raising average level is the only volume
    /// lever left on this hardware.
    /// </summary>
    public bool NormalizeLoudness { get; set; } = true;

    /// <summary>
    /// Raise the head clear of the speaker before speaking.
    /// </summary>
    /// <remarks>
    /// The speaker fires UPWARD from the centre of the chest, and the head at rest
    /// sits almost directly on top of it - so a resting Rose is physically gagged
    /// by her own head. Lifting the head is the single largest volume improvement
    /// available on this hardware, worth more than any DSP setting, and it costs
    /// nothing in voice quality.
    ///
    /// It also happens to be the right behaviour anyway: talking to someone with
    /// your face in your own chest looks broken.
    /// </remarks>
    public bool LiftHeadToSpeak { get; set; } = true;

    /// <summary>
    /// Maximum achievable head lift, in metres. Measured by commanding past it:
    /// 0.025, 0.030 and 0.040 all clamp to this same value.
    /// </summary>
    public const double MaxHeadLift = 0.0224;

    /// <summary>
    /// A line that has been synthesised and uploaded, ready to play on demand.
    /// </summary>
    /// <param name="SoundName">Name the clip was uploaded under.</param>
    /// <param name="Duration">How long it runs for.</param>
    public readonly record struct PreparedSpeech(string SoundName, TimeSpan Duration)
    {
        public bool IsEmpty => string.IsNullOrEmpty(SoundName);
    }

    /// <summary>
    /// Synthesises and uploads a line WITHOUT playing it.
    /// </summary>
    /// <remarks>
    /// Separated from playback so a caller can render the next sentence while the
    /// current one is still being spoken. Playing is near-instant once prepared, so
    /// this is what keeps multi-sentence replies gapless without overlapping them.
    /// </remarks>
    public async Task<PreparedSpeech> PrepareAsync(string text, Character character, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;

        var pcm = await _synth.SynthesizeAsync(text, GetVoice(character.Voice));
        if (NormalizeLoudness) Loudify(pcm);

        var temp = Path.Combine(Path.GetTempPath(), $"rose_{Guid.NewGuid():N}.wav");
        try
        {
            _synth.SaveAudioToFile(pcm, temp);
            var wav = await File.ReadAllBytesAsync(temp, ct);

            var name = Path.GetFileName(temp);
            using var ms = new MemoryStream(wav);
            await _rose.UploadSoundAsync(name, ms, ct);

            return new PreparedSpeech(name, WavDuration(wav));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Plays a prepared line and waits for it to finish.
    /// </summary>
    /// <remarks>
    /// The wait is the whole point. The daemon's play_sound returns as soon as
    /// playback is QUEUED, and starting another clip while one is still going cuts
    /// the first one off - which sounds like Rose interrupting herself a word or two
    /// into every sentence. Callers must let this complete before playing the next.
    /// </remarks>
    public async Task PlayAsync(PreparedSpeech speech, CancellationToken ct = default)
    {
        if (speech.IsEmpty) return;

        await LiftHeadAsync(ct);
        await _rose.PlaySoundAsync(speech.SoundName, ct);

        // A small tail beyond the sample length: the daemon starts playback slightly
        // after the call returns, and running the next line in on the final syllable
        // is exactly the artefact this is here to prevent.
        await Task.Delay(speech.Duration + TimeSpan.FromMilliseconds(250), ct);
    }

    private async Task LiftHeadAsync(CancellationToken ct)
    {
        if (!LiftHeadToSpeak) return;
        try
        {
            await _rose.GotoAsync(
                headPose: new XyzRpyPose(Z: MaxHeadLift, Pitch: -0.05),
                duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct);
        }
        catch { /* posture is an enhancement, never block speech on it */ }
    }

    /// <summary>
    /// Synthesises <paramref name="text"/> in the character's voice, plays it on Rose,
    /// and waits for playback to finish. Returns how long the audio ran for.
    /// </summary>
    public async Task<TimeSpan> SpeakAsync(string text, Character character, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;

        var prepared = await PrepareAsync(text, character, ct);
        await PlayAsync(prepared, ct);
        return prepared.Duration;
    }

    /// <summary>
    /// Playback duration of a RIFF/WAVE buffer, read from its own header.
    /// </summary>
    /// <remarks>
    /// Read rather than assumed, so it stays correct if the synthesiser's sample
    /// rate ever changes. Falls back to a length-based estimate at 24kHz mono - the
    /// Kokoro-82M output rate - if the header is not what we expect.
    /// </remarks>
    private static TimeSpan WavDuration(byte[] wav)
    {
        try
        {
            if (wav.Length >= 44 && wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F')
            {
                var byteRate = BitConverter.ToInt32(wav, 28);

                // Walk the chunk list rather than assuming data starts at 44 - a
                // LIST/INFO chunk before it is legal and would skew the estimate.
                var pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                    var size = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "data" && byteRate > 0)
                        return TimeSpan.FromSeconds(Math.Min(size, wav.Length - pos - 8) / (double)byteRate);
                    if (size <= 0) break;
                    pos += 8 + size + (size % 2);
                }
            }
        }
        catch { /* fall through to the estimate */ }

        return TimeSpan.FromSeconds(Math.Max(wav.Length - 44, 0) / 2.0 / 24000.0);
    }

    /// <summary>
    /// Peak-normalises and soft-compresses raw 16-bit PCM in place.
    /// </summary>
    /// <remarks>
    /// Operates on headerless PCM, which is what KokoroWavSynthesizer returns.
    /// Returns true if the buffer was modified.
    /// </remarks>
    internal static bool Loudify(byte[] pcm)
    {
        const int dataOffset = 0;
        if (pcm.Length < 2) return false;

        var sampleCount = pcm.Length / 2;
        var samples = new float[sampleCount];

        var peak = 0f;
        for (var i = 0; i < sampleCount; i++)
        {
            var s = BitConverter.ToInt16(pcm, dataOffset + i * 2) / 32768f;
            samples[i] = s;
            peak = Math.Max(peak, Math.Abs(s));
        }
        if (peak <= 0.0001f) return false;

        var wav = pcm;

        // Normalise first so the compressor threshold means the same thing
        // regardless of how hot the synthesiser happened to render this line.
        var norm = PeakTarget / peak;
        var postPeak = 0f;
        for (var i = 0; i < sampleCount; i++)
        {
            var s = samples[i] * norm;
            var mag = Math.Abs(s);
            if (mag > CompressorThreshold)
            {
                var over = mag - CompressorThreshold;
                mag = CompressorThreshold + over / CompressionRatio;
                s = Math.Sign(s) * mag;
            }
            samples[i] = s;
            postPeak = Math.Max(postPeak, Math.Abs(s));
        }

        // Compression lowered the peak; bring it back up to the target. This is
        // where the perceived loudness is actually won.
        var makeup = postPeak > 0.0001f ? PeakTarget / postPeak : 1f;
        for (var i = 0; i < sampleCount; i++)
        {
            var v = (int)MathF.Round(Math.Clamp(samples[i] * makeup, -1f, 1f) * 32767f);
            var b = BitConverter.GetBytes((short)v);
            wav[dataOffset + i * 2] = b[0];
            wav[dataOffset + i * 2 + 1] = b[1];
        }

        return true;
    }

    public void Dispose() => _synth?.Dispose();
}
