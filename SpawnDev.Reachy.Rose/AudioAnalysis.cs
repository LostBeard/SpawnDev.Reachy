namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Frame-level measurements of a speech clip: how loud, how high, how bright,
/// how steady, and how much is buried under it.
/// </summary>
/// <remarks>
/// This exists so reference-clip selection can be measured instead of guessed.
/// The distinction that matters for voice cloning is shouted-versus-spoken, and
/// three independent things move together when someone raises their voice:
/// fundamental frequency goes up (vocal folds tense), level goes up, and energy
/// shifts toward the top of the spectrum (the voice gets brighter, not just
/// louder - which is why a shout still sounds like a shout after you turn it
/// down). Measuring all three and combining them is far more reliable than any
/// one of them alone.
///
/// Everything here works on 16kHz mono float samples and allocates per clip, not
/// per frame. It is used offline during reference selection, never in the
/// conversation loop.
/// </remarks>
internal static class AudioAnalysis
{
    private const int FrameSize = 400;   // 25ms at 16kHz
    private const int HopSize = 160;     // 10ms
    private const int FftSize = 512;     // next power of two above the frame

    /// <param name="RmsDb">Level of the speech frames only, dBFS.</param>
    /// <param name="PeakDb">Highest sample in the clip, dBFS.</param>
    /// <param name="FrameDbStd">How much the level swings across speech frames, dB.</param>
    /// <param name="NoiseFloorDb">Level of the quietest tenth of frames - the bed under the dialogue.</param>
    /// <param name="HfRatio">Share of spectral energy above 2.5kHz. Brightness; rises when shouting.</param>
    /// <param name="MedianF0">Median fundamental frequency across voiced frames, Hz.</param>
    /// <param name="VoicedFraction">Share of the clip that is actually speech rather than gap.</param>
    public readonly record struct Features(
        double RmsDb,
        double PeakDb,
        double FrameDbStd,
        double NoiseFloorDb,
        double HfRatio,
        double MedianF0,
        double VoicedFraction);

    public static Features Measure(float[] samples, int rate)
    {
        if (samples.Length < FrameSize) return new Features(-90, -90, 0, -90, 0, 0, 0);

        var frameCount = 1 + (samples.Length - FrameSize) / HopSize;
        var frameDb = new double[frameCount];
        var peak = 0.0;

        for (var i = 0; i < frameCount; i++)
        {
            var off = i * HopSize;
            var sum = 0.0;
            for (var j = 0; j < FrameSize; j++)
            {
                double s = samples[off + j];
                sum += s * s;
                var mag = Math.Abs(s);
                if (mag > peak) peak = mag;
            }
            frameDb[i] = Db(Math.Sqrt(sum / FrameSize));
        }

        // The quietest tenth of the clip is whatever is playing UNDER the dialogue -
        // room tone, a music bed, an effect. That is what the denoiser will have to
        // remove, and what gets cloned as an echo if it cannot.
        var sorted = (double[])frameDb.Clone();
        Array.Sort(sorted);
        var noiseFloorDb = sorted[Math.Min(sorted.Length - 1, sorted.Length / 10)];

        // Speech frames sit clearly above that floor. A relative gate rather than an
        // absolute threshold, so it works on a quiet line and a loud one alike.
        var gate = Math.Max(noiseFloorDb + 8.0, -60.0);
        var speech = new List<int>(frameCount);
        for (var i = 0; i < frameCount; i++)
            if (frameDb[i] > gate) speech.Add(i);

        if (speech.Count == 0)
            return new Features(Db(0), Db(peak), 0, noiseFloorDb, 0, 0, 0);

        var speechDb = speech.Select(i => frameDb[i]).ToArray();
        var meanDb = speechDb.Average();
        var stdDb = Math.Sqrt(speechDb.Sum(d => (d - meanDb) * (d - meanDb)) / speechDb.Length);

        var hf = new List<double>(speech.Count);
        var f0 = new List<double>(speech.Count);
        var window = Hann(FrameSize);
        var re = new double[FftSize];
        var im = new double[FftSize];

        foreach (var i in speech)
        {
            var off = i * HopSize;
            hf.Add(HighFrequencyRatio(samples, off, window, rate, re, im));
            var pitch = EstimateF0(samples, off, rate);
            if (pitch > 0) f0.Add(pitch);
        }

        return new Features(
            RmsDb: meanDb,
            PeakDb: Db(peak),
            FrameDbStd: stdDb,
            NoiseFloorDb: noiseFloorDb,
            HfRatio: hf.Count > 0 ? hf.Average() : 0,
            MedianF0: f0.Count > 0 ? Median(f0) : 0,
            VoicedFraction: speech.Count / (double)frameCount);
    }

    /// <summary>
    /// Share of spectral energy above 2.5kHz, over the 100Hz-8kHz speech band.
    /// </summary>
    /// <remarks>
    /// This is the measurement that separates "loud because the mix is loud" from
    /// "loud because the character is shouting". Raising the gain on calm speech
    /// leaves this ratio untouched; actually shouting raises it, because a tense,
    /// pressed voice puts far more energy into the upper harmonics.
    /// </remarks>
    private static double HighFrequencyRatio(float[] samples, int offset, double[] window, int rate, double[] re, double[] im)
    {
        Array.Clear(re);
        Array.Clear(im);
        for (var i = 0; i < window.Length; i++) re[i] = samples[offset + i] * window[i];

        Fft(re, im);

        double total = 0, high = 0;
        var binHz = rate / (double)FftSize;
        for (var k = 1; k < FftSize / 2; k++)
        {
            var hz = k * binHz;
            if (hz < 100 || hz > 8000) continue;
            var power = re[k] * re[k] + im[k] * im[k];
            total += power;
            if (hz >= 2500) high += power;
        }
        return total > 0 ? high / total : 0;
    }

    /// <summary>
    /// Fundamental frequency by autocorrelation, or 0 if the frame is not voiced.
    /// </summary>
    /// <remarks>
    /// Autocorrelation rather than anything fancier because the decision it feeds is
    /// a ranking, not a measurement anyone reads in isolation - and unvoiced frames
    /// are rejected outright by the correlation threshold rather than contributing
    /// noise to the median. Searched over 70-400Hz, which covers the whole cast.
    /// </remarks>
    private static double EstimateF0(float[] samples, int offset, int rate)
    {
        var minLag = rate / 400;   // 400Hz
        var maxLag = rate / 70;    // 70Hz
        if (offset + maxLag + FrameSize > samples.Length) maxLag = samples.Length - offset - FrameSize;
        if (maxLag <= minLag) return 0;

        // Mean-removed, so a DC offset cannot masquerade as perfect periodicity.
        var mean = 0.0;
        for (var i = 0; i < FrameSize; i++) mean += samples[offset + i];
        mean /= FrameSize;

        var energy = 0.0;
        for (var i = 0; i < FrameSize; i++)
        {
            var v = samples[offset + i] - mean;
            energy += v * v;
        }
        if (energy < 1e-8) return 0;

        var bestLag = 0;
        var bestCorr = 0.0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var corr = 0.0;
            for (var i = 0; i < FrameSize; i++)
                corr += (samples[offset + i] - mean) * (samples[offset + i + lag] - mean);

            var norm = corr / energy;
            if (norm > bestCorr) { bestCorr = norm; bestLag = lag; }
        }

        // Below this the frame is unvoiced (a fricative, a gap, an effect) and its
        // "pitch" would be meaningless.
        return bestCorr >= 0.35 && bestLag > 0 ? rate / (double)bestLag : 0;
    }

    // ---- small DSP helpers --------------------------------------------------

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>In-place iterative radix-2 FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + len / 2] * curRe - im[i + j + len / 2] * curIm;
                    var vIm = re[i + j + len / 2] * curIm + im[i + j + len / 2] * curRe;

                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + len / 2] = uRe - vRe;
                    im[i + j + len / 2] = uIm - vIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    private static double Median(List<double> values)
    {
        var v = values.ToArray();
        Array.Sort(v);
        var mid = v.Length / 2;
        return v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
    }

    private static double Db(double amplitude) => amplitude <= 1e-9 ? -90 : 20 * Math.Log10(amplitude);
}
