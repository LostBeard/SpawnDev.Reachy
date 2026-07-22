using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Zero-shot voice cloning through sherpa-onnx ZipVoice: speaks new text in the
/// voice of a short reference clip.
/// </summary>
/// <remarks>
/// This is the show-voice path. ZipVoice is a flow-matching text-to-speech model
/// that conditions on a reference clip plus its transcript, so given a few clean
/// seconds of a character it will say anything in that voice - no per-voice
/// training, and entirely local. The reference clips come from
/// <see cref="VoiceBuilder"/>, which cuts them out of the show.
///
/// No new engine: sherpa-onnx is already a dependency for the ears, and it runs
/// the ONNX models itself. Output is 24kHz mono (the vocos vocoder's rate).
///
/// Private home use only - the reference audio impersonates real voice actors.
/// </remarks>
public sealed class RoseVoiceClone : IDisposable
{
    private readonly OfflineTts _tts;

    /// <summary>Output sample rate of the synthesiser (24kHz for the vocos vocoder).</summary>
    public int SampleRate => _tts.SampleRate;

    /// <summary>
    /// Flow-matching steps. The distill model is trained to sound right at a
    /// handful of steps; more steps trade time for a little quality.
    /// </summary>
    public int NumSteps { get; set; } = 4;

    /// <summary>The int8 ZipVoice package - the one the official example ships and tests, with a lexicon.</summary>
    private const string Int8Name = "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia";

    /// <summary>The fp32 package - higher quality, but shipped without a lexicon (copy one in).</summary>
    private const string Fp32Name = "sherpa-onnx-zipvoice-distill-zh-en-emilia";

    /// <param name="modelDir">Folder holding the ZipVoice model package(s).</param>
    /// <param name="fp32">Use the full-precision model instead of int8 - less quantization artifact.</param>
    /// <param name="numSteps">Flow-matching steps; more trades time for quality.</param>
    public RoseVoiceClone(string modelDir, bool fp32 = false, int numSteps = 4)
    {
        NumSteps = numSteps;
        var dir = Path.Combine(modelDir, fp32 ? Fp32Name : Int8Name);
        var config = new OfflineTtsConfig();
        config.Model.ZipVoice.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.ZipVoice.Encoder = Path.Combine(dir, fp32 ? "text_encoder.onnx" : "encoder.int8.onnx");
        config.Model.ZipVoice.Decoder = Path.Combine(dir, fp32 ? "fm_decoder.onnx" : "decoder.int8.onnx");
        // The vocoder is shared across packages; use whichever copy is on disk.
        config.Model.ZipVoice.Vocoder = ResolveVocoder(modelDir, dir);
        config.Model.ZipVoice.DataDir = Path.Combine(dir, "espeak-ng-data");
        config.Model.ZipVoice.Lexicon = Path.Combine(dir, "lexicon.txt");
        config.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
    }

    private static string ResolveVocoder(string modelDir, string dir)
    {
        var inPackage = Path.Combine(dir, "vocos_24khz.onnx");
        if (File.Exists(inPackage)) return inPackage;
        var alt = Path.Combine(modelDir, Fp32Name, "vocos_24khz.onnx");
        return File.Exists(alt) ? alt : inPackage;
    }

    /// <summary>True once the model files are present and the engine has loaded.</summary>
    public static bool ModelPresent(string modelDir) =>
        File.Exists(Path.Combine(modelDir, Int8Name, "decoder.int8.onnx"))
        && File.Exists(Path.Combine(modelDir, Int8Name, "lexicon.txt"));

    /// <summary>
    /// Speaks <paramref name="text"/> in the voice of <paramref name="reference"/>,
    /// returning 16-bit PCM at <see cref="SampleRate"/>.
    /// </summary>
    /// <param name="reference">Reference audio, mono float samples in [-1,1].</param>
    /// <param name="referenceSampleRate">Sample rate of the reference audio.</param>
    /// <param name="referenceText">Exact transcript of the reference audio.</param>
    public byte[] Clone(string text, float[] reference, int referenceSampleRate, string referenceText, float speed = 1.0f)
    {
        var gen = new OfflineTtsGenerationConfig
        {
            // A quarter second of trailing silence so the model does not carry the
            // last reference word into the start of the generated line.
            ReferenceAudio = PadTail(reference, referenceSampleRate / 4),
            ReferenceSampleRate = referenceSampleRate,
            ReferenceText = referenceText,
            NumSteps = NumSteps,
            Speed = speed,
            Sid = 0,
        };

        var audio = _tts.GenerateWithConfig(text, gen, null!);
        var samples = audio.Samples;
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        return pcm;
    }

    private static float[] PadTail(float[] samples, int silenceSamples)
    {
        var padded = new float[samples.Length + silenceSamples];
        Array.Copy(samples, padded, samples.Length);
        return padded;
    }

    public void Dispose() => _tts.Dispose();
}
