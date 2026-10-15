namespace SpawnDev.Reachy;

/// <summary>
/// A small FLAC encoder for speech clips on their way to the robot: the same samples, about half the bytes.
/// </summary>
/// <remarks>
/// <para>
/// Why: a clip goes up to the robot at roughly 100 KB/s, so the bytes ARE the wait before a sentence can play. FLAC is lossless, so the sound is the one the robot played from the WAV; the robot
/// stores a file under the extension in the upload's <c>encoding</c> ("flac-base64") and its player decodes by content.
/// </para>
/// <para>
/// Format: the FLAC spec's native stream - "fLaC", a STREAMINFO block (the MD5 left zero, which the spec allows for "unknown"), then frames. Each frame uses a fixed block size and per subframe the best of
/// CONSTANT, FIXED predictor order 0 to 4, or VERBATIM, with the residual Rice coded in the best partition order. Fixed predictors need no coefficient search and are what the reference encoder's
/// fastest levels use; for 24 kHz speech they land near half the size of the 16-bit PCM.
/// </para>
/// </remarks>
public static class FlacEncoder
{
    /// <summary>Samples per channel in a frame.</summary>
    public const int BlockSize = 4096;

    /// <summary>Encodes 16-bit PCM (interleaved when stereo) as a FLAC file.</summary>
    public static byte[] Encode(short[] pcm, int sampleRate, int channels = 1)
    {
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate is < 1 or > 655350) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var perChannel = pcm.Length / channels;
        var output = new MemoryStream(pcm.Length + 1024);

        output.Write("fLaC"u8);
        // STREAMINFO (the only metadata block, so it is the last): min/max block size, min/max frame size (0 = unknown), rate, channels, bits, total samples, MD5 (0 = unknown).
        var info = new BitWriter();
        var firstBlock = Math.Max(16, Math.Min(BlockSize, perChannel));    // a stream of one short frame states that frame's size
        info.Write(firstBlock, 16); info.Write(firstBlock, 16);
        info.Write(0, 24); info.Write(0, 24);
        info.Write(sampleRate, 20); info.Write(channels - 1, 3); info.Write(16 - 1, 5);
        info.Write((ulong)perChannel, 36);
        for (var i = 0; i < 16; i++) info.Write(0, 8);
        var infoBytes = info.ToArray();
        output.WriteByte(0x80);                                         // last-metadata-block flag + type 0 (STREAMINFO)
        output.WriteByte(0); output.WriteByte(0); output.WriteByte((byte)infoBytes.Length);
        output.Write(infoBytes);

        var channelSamples = new int[channels][];
        for (var c = 0; c < channels; c++) channelSamples[c] = new int[BlockSize];
        var frameNumber = 0;
        for (var start = 0; start < perChannel || frameNumber == 0; start += BlockSize, frameNumber++)
        {
            var n = Math.Min(BlockSize, perChannel - start);
            for (var c = 0; c < channels; c++)
                for (var i = 0; i < n; i++) channelSamples[c][i] = pcm[(start + i) * channels + c];

            var frame = new BitWriter();
            frame.Write(0x3FFE, 14);                                    // sync
            frame.Write(0, 1); frame.Write(0, 1);                       // reserved, fixed block size
            frame.Write(BlockSize == n ? 12 : 7, 4);                    // block size: 4096 (code 12), or "16 bit (size - 1) follows" for the short last frame
            var rateCode = RateCode(sampleRate);
            frame.Write(rateCode, 4);
            frame.Write(channels - 1, 4);                               // independent channels
            frame.Write(4, 3);                                          // 16 bits per sample
            frame.Write(0, 1);
            WriteUtf8(frame, frameNumber);
            if (BlockSize != n) frame.Write(Math.Max(n, 1) - 1, 16);
            if (rateCode == 12) frame.Write(sampleRate / 1000, 8);
            else if (rateCode == 13) frame.Write(sampleRate, 16);
            else if (rateCode == 14) frame.Write(sampleRate / 10, 16);
            var header = frame.ToArray(flush: false);
            frame.Write(Crc8(header), 8);                               // the header ends on a byte boundary

            for (var c = 0; c < channels; c++) WriteSubframe(frame, channelSamples[c], n);
            frame.Align();
            var body = frame.ToArray();
            output.Write(body);
            output.WriteByte((byte)(Crc16(body) >> 8)); output.WriteByte((byte)Crc16(body));
            if (perChannel == 0) break;
        }
        return output.ToArray();
    }

    // Sample-rate code of the frame header: a table entry when the rate is a common one, otherwise stored in the header.
    private static int RateCode(int rate) => rate switch
    {
        88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11,
        _ when rate % 1000 == 0 && rate / 1000 <= 255 => 12,
        _ when rate <= 65535 => 13,
        _ => 14,
    };

    private static void WriteSubframe(BitWriter w, int[] x, int n)
    {
        // CONSTANT: digital silence and DC take almost nothing.
        var constant = true;
        for (var i = 1; i < n && constant; i++) constant = x[i] == x[0];
        if (constant) { w.Write(0, 1); w.Write(0b000000, 6); w.Write(0, 1); w.Write(x[0], 16); return; }

        var best = -1; long bestCost = long.MaxValue; int[]? bestResidual = null; var bestPartition = 0; int[]? bestParams = null;
        for (var order = 0; order <= 4 && order < n; order++)
        {
            var residual = Residual(x, n, order);
            var (cost, partitionOrder, parameters) = PlanRice(residual, order, n);
            cost += order * 16L;                                        // warm-up samples
            if (cost < bestCost) { bestCost = cost; best = order; bestResidual = residual; bestPartition = partitionOrder; bestParams = parameters; }
        }
        if (best < 0 || bestCost >= (long)n * 16)
        {
            // VERBATIM: noise that does not compress.
            w.Write(0, 1); w.Write(0b000001, 6); w.Write(0, 1);
            for (var i = 0; i < n; i++) w.Write(x[i], 16);
            return;
        }

        w.Write(0, 1); w.Write(0b001000 | best, 6); w.Write(0, 1);      // FIXED, order 'best'
        for (var i = 0; i < best; i++) w.Write(x[i], 16);               // warm-up
        WriteRice(w, bestResidual!, best, n, bestPartition, bestParams!);
    }

    // The residual of a fixed predictor: order 0 is the signal, each further order is one more difference.
    private static int[] Residual(int[] x, int n, int order)
    {
        var current = new int[n];
        Array.Copy(x, current, n);
        for (var pass = 0; pass < order; pass++)
            for (var i = n - 1; i > pass; i--) current[i] -= current[i - 1];
        return current;                                                 // the first 'order' entries are not residuals
    }

    private static uint Zigzag(int v) => (uint)((v << 1) ^ (v >> 31));

    // Chooses the partition order and per-partition Rice parameters with the fewest bits. Returns (bits, partition order, parameters).
    private static (long Bits, int PartitionOrder, int[] Parameters) PlanRice(int[] residual, int order, int n)
    {
        long bestBits = long.MaxValue; var bestOrder = 0; int[] bestParams = [0];
        for (var po = 0; po <= 8; po++)
        {
            var parts = 1 << po;
            if (n % parts != 0 || n / parts <= order) break;
            var size = n / parts;
            var parameters = new int[parts];
            long bits = 4 + 4;                                          // coding method (2) + partition order (4), counted loosely with the parameter fields below
            for (var p = 0; p < parts; p++)
            {
                var from = p * size + (p == 0 ? order : 0);
                var to = (p + 1) * size;
                long sum = 0; var count = to - from;
                for (var i = from; i < to; i++) sum += Zigzag(residual[i]);
                var k = 0;
                if (count > 0) while (k < 14 && ((long)count << (k + 1)) < sum) k++;
                // Bits at parameter k: unary quotient + stop bit + k remainder bits, per sample; refine by trying k-1 and k+1.
                long Cost(int kk)
                {
                    long c = 0;
                    for (var i = from; i < to; i++) c += (Zigzag(residual[i]) >> kk) + 1 + kk;
                    return c;
                }
                var bk = k; var bc = Cost(k);
                foreach (var alt in new[] { k - 1, k + 1 })
                    if (alt is >= 0 and <= 14) { var cc = Cost(alt); if (cc < bc) { bc = cc; bk = alt; } }
                parameters[p] = bk; bits += 4 + bc;
            }
            if (bits < bestBits) { bestBits = bits; bestOrder = po; bestParams = parameters; }
        }
        return (bestBits + 2 + 4, bestOrder, bestParams);
    }

    private static void WriteRice(BitWriter w, int[] residual, int order, int n, int partitionOrder, int[] parameters)
    {
        w.Write(0, 2);                                                  // Rice coding with 4-bit parameters
        w.Write(partitionOrder, 4);
        var size = n >> partitionOrder;
        for (var p = 0; p < parameters.Length; p++)
        {
            var k = parameters[p];
            w.Write(k, 4);
            var from = p * size + (p == 0 ? order : 0);
            var to = (p + 1) * size;
            for (var i = from; i < to; i++)
            {
                var u = Zigzag(residual[i]);
                var q = (int)(u >> k);
                w.WriteUnary(q);
                if (k > 0) w.Write((int)(u & ((1u << k) - 1)), k);
            }
        }
    }

    // FLAC's UTF-8 style coded number of the frame (or sample) index.
    private static void WriteUtf8(BitWriter w, int value)
    {
        if (value < 0x80) { w.Write(value, 8); return; }
        var extra = value < 0x800 ? 1 : value < 0x10000 ? 2 : value < 0x200000 ? 3 : value < 0x4000000 ? 4 : 5;
        w.Write((0xFF00 >> (extra + 1)) & 0xFF | (value >> (6 * extra)), 8);
        for (var i = extra - 1; i >= 0; i--) w.Write(0x80 | ((value >> (6 * i)) & 0x3F), 8);
    }

    private static int Crc8(byte[] bytes)
    {
        var crc = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (var b = 0; b < 8; b++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return crc;
    }

    private static int Crc16(byte[] bytes)
    {
        var crc = 0;
        foreach (var b in bytes)
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }

    private sealed class BitWriter
    {
        private readonly MemoryStream _bytes = new();
        private ulong _acc;
        private int _bits;

        public void Write(int value, int bits) => Write((ulong)(uint)value & (bits >= 32 ? 0xFFFFFFFFul : ((1ul << bits) - 1)), bits);

        public void Write(ulong value, int bits)
        {
            if (bits < 64) value &= (1ul << bits) - 1;
            for (var i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_bits == 8) { _bytes.WriteByte((byte)_acc); _acc = 0; _bits = 0; }
            }
        }

        public void WriteUnary(int zeros)
        {
            for (var i = 0; i < zeros; i++) Write(0, 1);
            Write(1, 1);
        }

        public void Align() { while (_bits != 0) Write(0, 1); }

        public byte[] ToArray(bool flush = true)
        {
            if (flush) { Align(); }
            return _bytes.ToArray();
        }
    }
}
