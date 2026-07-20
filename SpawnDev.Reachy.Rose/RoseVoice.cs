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

    /// <summary>Synthesises <paramref name="text"/> in the character's voice and plays it on Rose.</summary>
    public async Task SpeakAsync(string text, Character character, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // SynthesizeAsync returns RAW 16-bit PCM with no container - the RIFF
        // header is added by SaveAudioToFile. Uploading these bytes directly gets
        // "Unsupported or invalid audio file" from the daemon, which validates
        // uploads by content.
        // Start the head moving BEFORE synthesis rather than after: the lift and
        // the TTS run concurrently, so the posture is already correct by the time
        // there is audio to play and costs no added latency.
        var lift = LiftHeadToSpeak
            ? _rose.GotoAsync(
                headPose: new XyzRpyPose(Z: MaxHeadLift, Pitch: -0.05),
                duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct)
            : Task.FromResult<MoveHandle?>(null);

        var pcm = await _synth.SynthesizeAsync(text, GetVoice(character.Voice));
        if (NormalizeLoudness) Loudify(pcm);
        try { await lift; } catch { /* posture is an enhancement, never block speech on it */ }

        // Round-trip through their writer rather than hand-rolling a header, so
        // the sample rate and fmt chunk always match what the model produced.
        var temp = Path.Combine(Path.GetTempPath(), $"rose_{Guid.NewGuid():N}.wav");
        try
        {
            _synth.SaveAudioToFile(pcm, temp);
            var wav = await File.ReadAllBytesAsync(temp, ct);

            // Unique name per utterance so a play cannot race an overwrite of the
            // previous one.
            var name = Path.GetFileName(temp);
            using (var ms = new MemoryStream(wav))
                await _rose.UploadSoundAsync(name, ms, ct);

            await _rose.PlaySoundAsync(name, ct);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
