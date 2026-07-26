using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Decides which character an unlabelled caption belongs to, by voice.
/// </summary>
/// <remarks>
/// The closed captions only write <c>[N]</c> when the speaker would otherwise be
/// ambiguous, so across a whole season a character gets a handful of labelled lines
/// and hundreds of unlabelled ones. Ranking five candidates for calmness cannot find
/// a calm line if none of the five is calm - the pool has to be bigger first.
///
/// This is a speaker-embedding classifier, not a diarizer, and the difference is the
/// entire point. An earlier attempt ran full diarization and cut reference audio at
/// the DIARIZATION boundaries while taking the transcript from overlapping captions;
/// the two disagreed, and ZipVoice bled the unaccounted-for words into every line it
/// generated. Here the cut is never touched: a candidate is always exactly one
/// caption run's audio window paired with that same caption run's text. The
/// embedding only supplies a NAME for a window that was already going to be cut that
/// way. Misidentification therefore costs a wrong entry in a shortlist a human is
/// about to listen to - it cannot produce a misaligned reference.
///
/// Uses the wespeaker CAM++ model already on disk.
/// </remarks>
internal sealed class SpeakerMatcher : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public int Dim => _extractor.Dim;

    private SpeakerMatcher(SpeakerEmbeddingExtractor extractor) => _extractor = extractor;

    /// <summary>Loads the embedding model, or returns null if it is not on disk.</summary>
    public static SpeakerMatcher? Create(string modelDir)
    {
        var model = Path.Combine(modelDir, "wespeaker_en_voxceleb_CAM++.onnx");
        if (!File.Exists(model)) return null;

        var cfg = new SpeakerEmbeddingExtractorConfig
        {
            Model = model,
            NumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            Provider = "cpu",
            Debug = 0,
        };
        return new SpeakerMatcher(new SpeakerEmbeddingExtractor(cfg));
    }

    /// <summary>
    /// A unit-length embedding of one clip, or null if the model could not use it.
    /// </summary>
    /// <remarks>
    /// Normalised on the way out so every later comparison is a plain dot product,
    /// and so averaging embeddings into a centroid weights each clip equally rather
    /// than by however loud it happened to be.
    /// </remarks>
    public float[]? Embed(float[] samples, int rate)
    {
        if (samples.Length < rate / 2) return null;   // under half a second is not a voice

        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(rate, samples);
        stream.InputFinished();
        if (!_extractor.IsReady(stream)) return null;

        var v = _extractor.Compute(stream);
        return v is { Length: > 0 } ? Normalize(v) : null;
    }

    /// <summary>Mean of a set of unit vectors, renormalised - the centre of a speaker's voice.</summary>
    public static float[] Centroid(IReadOnlyList<float[]> embeddings)
    {
        var sum = new float[embeddings[0].Length];
        foreach (var e in embeddings)
            for (var i = 0; i < sum.Length; i++) sum[i] += e[i];
        return Normalize(sum);
    }

    /// <summary>Cosine similarity of two unit vectors, in [-1,1].</summary>
    public static double Similarity(float[] a, float[] b)
    {
        var dot = 0.0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) dot += a[i] * b[i];
        return dot;
    }

    private static float[] Normalize(float[] v)
    {
        var norm = 0.0;
        foreach (var x in v) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm < 1e-9) return v;

        var outv = new float[v.Length];
        for (var i = 0; i < v.Length; i++) outv[i] = (float)(v[i] / norm);
        return outv;
    }

    public void Dispose() => _extractor.Dispose();
}
