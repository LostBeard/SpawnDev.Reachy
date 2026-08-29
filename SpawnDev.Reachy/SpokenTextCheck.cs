namespace SpawnDev.Reachy;

/// <summary>
/// Compares what the synthesiser was ASKED to say with what the recogniser HEARD.
/// </summary>
/// <remarks>
/// <para>
/// This is the measurement behind speaking verification: a zero-shot cloner draws fresh
/// noise for every render, and some draws come back as words nobody asked for. Scoring
/// a render against its own text is how that is caught without a person listening.
/// </para>
/// <para>
/// The comparison skips freely at the head and tail of the transcript. That is not
/// leniency, it is necessary: the cloner regenerates part of the reference clip's own
/// speech ahead of the line it was asked to speak, so a transcript can open with a few
/// words of the voice being copied. Charging for those would put a floor under every
/// score and bury the thing being measured. Everything INSIDE the sentence - every
/// substitution, deletion and insertion - is charged in full.
/// </para>
/// <para>
/// It hears wrong WORDS only. It cannot hear an accent, an odd rhythm, a stutter or an
/// audible breath. Treat a clean result as "the words survived", never as "it sounds
/// right" - the pitch guard and a human ear cover what this cannot.
/// </para>
/// <para>
/// Ported from SpawnDev.ILGPU.ML's SpokenTextCheck, where the policy was measured. Rose
/// speaks through sherpa-onnx rather than that library, so the algorithm travels rather
/// than the dependency.
/// </para>
/// </remarks>
public static class SpokenTextCheck
{
    /// <summary>
    /// Word error rate of <paramref name="heard"/> against <paramref name="expected"/>,
    /// 0 (every word survived) to 1.
    /// </summary>
    public static double WordErrorRate(string expected, string heard)
    {
        var truth = Words(expected);
        var hypothesis = Words(heard);
        if (truth.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

        // Row zero is all zeros, so starting anywhere in the transcript is free; taking
        // the minimum of the last row makes ending anywhere free too.
        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var i = 1; i <= truth.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                                      previous[j - 1] + (truth[i - 1] == hypothesis[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous.Min() / (double)truth.Length;
    }

    /// <summary>
    /// Word error rate that charges for EVERYTHING, including words before and after the
    /// sentence.
    /// </summary>
    /// <remarks>
    /// The strict counterpart of <see cref="WordErrorRate"/>. Use it when the leading
    /// words are the thing being measured rather than an artifact to see past - as when
    /// checking whether a reference clip is bleeding into the start of every render.
    /// </remarks>
    public static double WordErrorRateStrict(string expected, string heard)
    {
        var truth = Words(expected);
        var hypothesis = Words(heard);
        if (truth.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++) previous[j] = j;
        for (var i = 1; i <= truth.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                                      previous[j - 1] + (truth[i - 1] == hypothesis[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[hypothesis.Length] / (double)truth.Length;
    }

    /// <summary>
    /// Splits text into comparable words: lower case, punctuation dropped, apostrophes
    /// kept so "don't" stays one word.
    /// </summary>
    private static string[] Words(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
