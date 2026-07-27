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

    /// <summary>The show-voice cloner, or null when running on Kokoro voices alone.</summary>
    private readonly RoseVoiceClone? _clone;

    /// <summary>Reference clip + its exact transcript, per character that has one.</summary>
    private readonly Dictionary<string, (float[] Samples, int Rate, string Text)> _voiceprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lines already synthesised and uploaded this session, by content.</summary>
    private readonly Dictionary<string, PreparedSpeech> _prepared = [];

    /// <summary>Sound names the robot already holds, so a known line is never re-uploaded.</summary>
    private HashSet<string>? _onRobot;

    private readonly string? _cacheDir;

    /// <summary>Sample rate of Kokoro-82M's output.</summary>
    private const int KokoroRate = 24000;

    /// <summary>Characters that will speak in their own show voice rather than a Kokoro voice.</summary>
    public IReadOnlyCollection<string> ClonedCharacters => _voiceprints.Keys;

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

    /// <param name="rose">The robot the audio comes out of.</param>
    /// <param name="modelPath">Explicit path to kokoro.onnx, or null to discover it.</param>
    /// <param name="cloneVoices">
    /// Speak as the actual show characters, for every character that has a reference
    /// clip in models/voiceprints. Characters without one keep their Kokoro voice, so
    /// this degrades to the old behaviour rather than failing.
    /// </param>
    /// <param name="cloneSteps">
    /// Flow-matching steps for the cloner. 16, because that is the setting the clean
    /// recipe was confirmed on. Fewer steps render faster - 4 is roughly real time -
    /// but they leave the echoey, reverby quality this pipeline exists to avoid.
    /// Speed is not worth reintroducing it; if a line needs to be instant, pre-generate
    /// it and let the cache serve it.
    /// </param>
    /// <param name="fp32">Full-precision cloner. int8 is faster but leaves an echo tinge.</param>
    /// <param name="cloneProvider">
    /// onnxruntime execution provider for the cloner: "cuda" runs ZipVoice on the GPU
    /// (~9s/line becomes a fraction of a second), "cpu" keeps it on the processor.
    /// Safe to ask for "cuda" always - sherpa falls back to CPU when the CUDA stack is
    /// not present (see gpu-setup/GPU-SETUP.md).
    /// </param>
    public RoseVoice(ReachyMiniClient rose, string? modelPath = null,
                     bool cloneVoices = false, int cloneSteps = 16, bool fp32 = true,
                     string cloneProvider = "cuda")
    {
        _rose = rose;
        _synth = new KokoroWavSynthesizer(modelPath ?? ResolveModelPath());

        if (!cloneVoices) return;

        var modelDir = ResolveModelDir();
        if (modelDir is null) return;

        _cacheDir = Path.Combine(modelDir, "voicecache");
        LoadVoiceprints(Path.Combine(modelDir, "voiceprints"));
        if (_voiceprints.Count > 0)
        {
            _clone = new RoseVoiceClone(modelDir, fp32, cloneSteps, cloneProvider);
            // Guard every live render against the zero-shot gender drift that made N
            // switch to a female voice mid-conversation. Diagnostics leave this off to
            // measure the raw drift; the conversation must never expose it to Aubs.
            _clone.StabilizePitch = true;
        }
    }

    /// <summary>
    /// Loads every reference clip on disk. A character without one simply is not in
    /// the dictionary and falls back to Kokoro.
    /// </summary>
    private void LoadVoiceprints(string dir)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var c in CharacterLibrary.All)
        {
            var wav = Path.Combine(dir, $"{c.Name}.wav");
            var txt = Path.Combine(dir, $"{c.Name}.txt");
            if (!File.Exists(wav) || !File.Exists(txt)) continue;

            var (samples, rate) = ShowAudio.ReadWav(wav);
            var text = File.ReadAllText(txt).Trim();

            // A reference whose transcript does not match its audio makes the cloner
            // leak the unaccounted-for words into every line it speaks, so an empty or
            // missing transcript is skipped rather than guessed at.
            if (samples.Length > 0 && text.Length > 0)
                _voiceprints[c.Name] = (samples, rate, text);
        }
    }

    private static string? ResolveModelDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var models = Path.Combine(d.FullName, "models");
            if (Directory.Exists(models)) return models;
        }
        return null;
    }

    /// <summary>
    /// Locates kokoro.onnx, downloading it via KokoroTTS if absent. The library
    /// drops it in the process working directory rather than a package cache, so
    /// the path has to be discovered rather than assumed.
    /// </summary>
    private static string ResolveModelPath()
    {
        // Walk up from the binary so the file is found regardless of the working
        // directory - autostart runs us from C:\Windows\System32, where neither the
        // file nor a fresh download of it belongs. The cwd is also checked (it is
        // pinned to the app root at startup) before falling back to downloading.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "kokoro.onnx");
            if (File.Exists(candidate)) return candidate;
        }
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "kokoro.onnx");
        if (File.Exists(cwd)) return cwd;

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
    /// <param name="bypassCache">
    /// Render even if this exact line is already prepared. Only for measuring what a
    /// fresh render costs - a timing test that silently returns a cached clip reports
    /// a speed the conversation will never actually see.
    /// </param>
    public async Task<PreparedSpeech> PrepareAsync(string text, Character character, CancellationToken ct = default, bool bypassCache = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;

        // Named by content, not by a GUID. Cloned synthesis is slow and CPU-bound, so
        // the same line said twice must cost nothing the second time - and a stable
        // name means a line pre-generated in an earlier session is still on the robot
        // and still usable, which is what keeps a greeting instant.
        var key = ContentKey(text, character);
        if (!bypassCache)
        {
            if (_prepared.TryGetValue(key, out var cached)) return cached;

            if (await AlreadyOnRobotAsync($"rose_{key}.wav", key, ct) is { } known)
            {
                _prepared[key] = known;
                return known;
            }
        }

        var soundName = $"rose_{key}.wav";

        var (pcm, rate, wasCloned) = await RenderAsync(text, character, ct);
        if (pcm.Length == 0) return default;

        // Kokoro output is clean, so compressing it is free loudness. A cloned voice is
        // NOT clean in the same way - it carries a little low-level tail, and pulling
        // that up with makeup gain is what makes it sound reverby. The clean-clone
        // recipe never had a compressor in it, so cloned audio does not get one.
        if (NormalizeLoudness && !wasCloned) Loudify(pcm);

        var wav = BuildWav(pcm, rate);
        using var ms = new MemoryStream(wav);
        await _rose.UploadSoundAsync(soundName, ms, ct);

        var prepared = new PreparedSpeech(soundName, TimeSpan.FromSeconds(pcm.Length / 2.0 / rate));
        _prepared[key] = prepared;
        _onRobot?.Add(soundName);
        RememberDuration(key, prepared.Duration);
        return prepared;
    }

    /// <summary>
    /// Renders a line, in the character's own show voice when we have a reference for
    /// them, otherwise in their Kokoro voice.
    /// </summary>
    /// <remarks>
    /// The cloner is synchronous and CPU-bound, so it runs off the calling thread -
    /// otherwise it would block the conversation loop's own timers while it works.
    /// Falling back to Kokoro rather than throwing matters: a character whose
    /// reference has not been chosen yet should still talk to Aubs.
    /// </remarks>
    private async Task<(byte[] Pcm, int Rate, bool Cloned)> RenderAsync(string text, Character character, CancellationToken ct)
    {
        if (_clone is not null && _voiceprints.TryGetValue(character.Name, out var vp))
        {
            // Per-character pitch bounds (N is a pre-teen boy: near the female line at
            // the top, and prone to rendering too deep at the bottom). Renders are
            // serialised through the conversation, so setting these per call is safe.
            // 0 leaves that side of the guard bounded by the reference pitch alone.
            _clone.PitchCeiling = character.PitchCeilingHz ?? 0;
            _clone.PitchFloor = character.PitchFloorHz ?? 0;
            var pcm = await Task.Run(() => _clone.Clone(text, vp.Samples, vp.Rate, vp.Text), ct);
            return (pcm, _clone.SampleRate, true);
        }

        return (await _synth.SynthesizeAsync(text, GetVoice(character.Voice)), KokoroRate, false);
    }

    /// <summary>A stable short name for this exact line in this exact voice.</summary>
    /// <remarks>
    /// The pitch-guard bounds are part of the key: a clip cached under an earlier,
    /// looser guard would otherwise be served forever even after the voice was
    /// retuned - which is exactly how a too-deep greeting got frozen and kept playing.
    /// Folding the bounds in means a tuning change re-renders rather than replays.
    /// </remarks>
    private string ContentKey(string text, Character character)
    {
        var cloned = _voiceprints.ContainsKey(character.Name);
        var voice = cloned ? $"clone:{character.Name}" : $"kokoro:{character.Voice}";
        var tuning = cloned ? $"|c{character.PitchCeilingHz ?? 0}|f{character.PitchFloorHz ?? 0}" : "";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{voice}|{NormalizeLoudness}{tuning}|{text.Trim()}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The prepared line if the robot already holds it from an earlier session.
    /// </summary>
    /// <remarks>
    /// Playback needs the clip's duration (that is how non-overlap is enforced), and
    /// re-synthesising just to measure it would defeat the point, so durations are
    /// written next to the cache key when a line is first made.
    /// </remarks>
    private async Task<PreparedSpeech?> AlreadyOnRobotAsync(string soundName, string key, CancellationToken ct)
    {
        var known = RecallDuration(key);
        if (known is null) return null;

        if (_onRobot is null)
        {
            try
            {
                var listed = await _rose.ListSoundsAsync(ct);
                _onRobot = listed is null
                    ? []
                    : new HashSet<string>(listed.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            }
            catch { _onRobot = []; }   // if we cannot tell, just re-upload
        }

        return _onRobot.Contains(soundName) ? new PreparedSpeech(soundName, known.Value) : null;
    }

    private void RememberDuration(string key, TimeSpan duration)
    {
        if (_cacheDir is null) return;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(Path.Combine(_cacheDir, $"{key}.txt"), duration.TotalSeconds.ToString("R"));
        }
        catch { /* the cache is an optimisation, never a requirement */ }
    }

    private TimeSpan? RecallDuration(string key)
    {
        if (_cacheDir is null) return null;
        try
        {
            var path = Path.Combine(_cacheDir, $"{key}.txt");
            if (File.Exists(path) && double.TryParse(File.ReadAllText(path), out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        catch { /* fall through and re-synthesise */ }
        return null;
    }

    /// <summary>Wraps raw 16-bit mono PCM in a RIFF header at the given rate.</summary>
    private static byte[] BuildWav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        }
        return ms.ToArray();
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

    public void Dispose()
    {
        _synth?.Dispose();
        _clone?.Dispose();
    }
}
