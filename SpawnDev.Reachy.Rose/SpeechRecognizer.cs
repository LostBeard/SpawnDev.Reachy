using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// One loaded Whisper model, safe to transcribe from more than one thread.
/// </summary>
/// <remarks>
/// Split out of <see cref="RoseEars"/> because Rose transcribes for two different reasons
/// and they want different models. The microphone needs the most accurate model that fits
/// the latency budget, because it is listening to a ten year old in a room. Checking her
/// own renders wants the OPPOSITE quality: a model that reports what is actually in the
/// audio rather than one clever enough to repair it.
///
/// That is not a preference, it is measured. Given a render that had collapsed into
/// "Can Can Can We Can We Can We Can", small.en wrote back "Hey, can we talk about
/// something else, please?" - it reconstructed the sentence that was meant and scored the
/// clip perfect. base.en transcribed the repetition faithfully and scored it 75% wrong. A
/// recogniser good enough to fix the defect cannot be used to detect it.
///
/// One class with two instances rather than two mechanisms: both paths load the same way,
/// serialise the same way, and there is one place to fix when that changes.
/// </remarks>
internal sealed class SpeechRecognizer : IDisposable
{
    private readonly OfflineRecognizer _asr;

    /// <summary>
    /// Serialises decoding. One recogniser may be asked for the microphone on the ears'
    /// worker thread and for a render on the synthesis thread at the same time, and one
    /// OfflineRecognizer decoding two streams at once is not something to assume is safe.
    /// </summary>
    private readonly object _lock = new();

    private bool _disposed;

    /// <summary>The Whisper model actually loaded, which may not be the one asked for.</summary>
    public string Model { get; }

    /// <param name="modelDir">Folder holding <c>sherpa-onnx-whisper-*</c>.</param>
    /// <param name="whisperModel">
    /// Which Whisper model, e.g. "small.en" or "base.en". Falls back to base.en, which the
    /// fetch step always installs, when the requested one is not on disk.
    /// </param>
    /// <param name="threads">Thread budget on the CPU provider.</param>
    /// <param name="provider">
    /// onnxruntime execution provider, "cpu" (default) or "cuda". CPU is the default on
    /// purpose: the GPU already holds the language model and the voice cloner, and on a 12GB
    /// card adding Whisper too pushed VRAM to ~96% and a starved CUDA op wedged the whole
    /// conversation. Pass "cuda" only on a machine with VRAM to spare.
    /// </param>
    /// <param name="quantization">"int8" (default), "fp32", or null for the default.</param>
    public SpeechRecognizer(string modelDir, string whisperModel = "small.en", int threads = 4,
                            string provider = "cpu", string? quantization = null)
    {
        var whisperName = whisperModel;
        var whisperDir = Path.Combine(modelDir, $"sherpa-onnx-whisper-{whisperName}");
        if (!Directory.Exists(whisperDir))
        {
            // Not downloaded yet - fall back to base.en, which fetch_models always installs.
            whisperName = "base.en";
            whisperDir = Path.Combine(modelDir, "sherpa-onnx-whisper-base.en");
        }

        if (!Directory.Exists(whisperDir))
            throw new DirectoryNotFoundException($"Whisper model dir not found: {whisperDir}");

        Model = whisperName;

        // onnxruntime loads cuDNN by bare name from the exe dir + PATH, not the
        // runtimes/native subfolder where the CUDA build lives, so make it findable
        // before creating the recognizer (same as the voice cloner does).
        if (provider == "cuda") RoseVoiceClone.EnsureCudaLibrariesFindable();

        // Use the int8 weights on BOTH providers. The fp32 Whisper decoder crashes the
        // onnxruntime 1.27 CUDA EP (native SEHException on load - fp32+CPU and int8+CUDA
        // both work, only fp32+CUDA faults), so int8 is the working GPU path here, and it
        // still runs on the card ~1.7x faster than on CPU (measured: 2.7s vs 4.7s on a
        // 16s clip) at small.en accuracy. `quantization:"fp32"` forces the full-precision
        // weights for when a future onnxruntime fixes the CUDA fault. Both ship in the package.
        var useInt8 = quantization switch
        {
            "int8" => true,
            "fp32" => false,
            _ => true,
        };
        var quant = useInt8 ? ".int8" : "";

        var asrConfig = new OfflineRecognizerConfig();
        asrConfig.ModelConfig.Whisper.Encoder = Path.Combine(whisperDir, $"{whisperName}-encoder{quant}.onnx");
        asrConfig.ModelConfig.Whisper.Decoder = Path.Combine(whisperDir, $"{whisperName}-decoder{quant}.onnx");
        asrConfig.ModelConfig.Tokens = Path.Combine(whisperDir, $"{whisperName}-tokens.txt");
        asrConfig.ModelConfig.ModelType = "whisper";
        // On GPU the flow runs on the card, so a couple of threads is plenty; on CPU use
        // the caller's thread budget.
        asrConfig.ModelConfig.NumThreads = provider == "cuda" ? 2 : threads;
        asrConfig.ModelConfig.Provider = provider;
        // 1 = sherpa prints its "available providers" line so we can SEE whether CUDA
        // actually engaged rather than assuming it did.
        asrConfig.ModelConfig.Debug = provider == "cuda" ? 1 : 0;
        asrConfig.DecodingMethod = "greedy_search";

        _asr = new OfflineRecognizer(asrConfig);
    }

    /// <summary>
    /// Transcribes one clip. Returns an empty string once disposed.
    /// </summary>
    /// <remarks>
    /// The clip's own sample rate is passed straight through: sherpa builds a resampler
    /// inside AcceptWaveform when it differs from the rate the model wants, and says so in
    /// its log. The microphone runs at 16kHz and the synthesiser renders at 24kHz, so this
    /// is load-bearing - and it is why there is no resampling code here.
    /// </remarks>
    public string Transcribe(float[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0) return "";
        lock (_lock)
        {
            if (_disposed) return "";
            using var stream = _asr.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            _asr.Decode(stream);
            return stream.Result.Text?.Trim() ?? "";
        }
    }

    public void Dispose()
    {
        // Under the transcription lock, so a decode in flight on another thread can never
        // be working on a recogniser being torn down underneath it.
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _asr.Dispose();
        }
    }
}
