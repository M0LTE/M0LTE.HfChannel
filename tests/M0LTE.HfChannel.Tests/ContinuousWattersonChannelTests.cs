using M0LTE.Ofdm;

namespace M0LTE.HfChannel.Tests;

public class ContinuousWattersonChannelTests
{
    private const int Rate = 9600;

    /// <summary>Bursts of in-band noise on the 1800 Hz carrier with silence between them,
    /// as a 5066 exchange puts on the air.</summary>
    private static float[] BurstsAndGaps(int seed, int bursts, int burstSamples, int gapSamples)
    {
        var random = new Random(seed);
        var signal = new float[bursts * (burstSamples + gapSamples)];
        for (int b = 0; b < bursts; b++)
        {
            int start = b * (burstSamples + gapSamples);
            for (int i = 0; i < burstSamples; i++)
            {
                int n = start + i;
                signal[n] = (float)((random.NextDouble() - 0.5) * Math.Sin(2 * Math.PI * 1800 * n / (double)Rate));
            }
        }

        return signal;
    }

    private static ContinuousWattersonChannel Full(int seed) =>
        new(Rate, seed, snrDb: 12, signalPower: 0.02, WattersonChannel.Poor)
        {
            FrequencyOffsetHz = 1.5,
            TransmitPassband = new SsbPassband(),
            ReceivePassband = new SsbPassband(500, 2700),
        };

    [Fact]
    public void Streamed_Fading_Is_The_Batch_Process_Bit_For_Bit()
    {
        // The same random stream through the batch generator and the streaming one gives
        // the same gains, so the fading statistics of Poor (and every preset) are unchanged.
        foreach (double sigmaHz in new[] { 0.5, 0.25 })
        {
            Cf[] batch = new WattersonChannel(Rate, seed: 21).FadingGains(Rate * 30, sigmaHz);
            var stream = new FadingProcess(new Random(21), sigmaHz, Rate);
            for (int i = 0; i < batch.Length; i++)
            {
                Cf g = stream.Next();
                if (g != batch[i])
                {
                    Assert.Fail($"gain {i} differs: {g} streamed, {batch[i]} batch");
                }
            }
        }
    }

    [Fact]
    public void Output_Does_Not_Depend_On_How_The_Input_Is_Split_Into_Calls()
    {
        // One continuous realisation: fading, noise and filter state carry across calls.
        float[] input = BurstsAndGaps(seed: 2, bursts: 4, burstSamples: Rate * 3, gapSamples: Rate * 2);

        float[] oneCall = Full(seed: 9).Process(input);

        var chunked = Full(seed: 9);
        var pieces = new float[input.Length];
        var random = new Random(4);
        int at = 0;
        while (at < input.Length)
        {
            int size = Math.Min(random.Next(1, 5000), input.Length - at);
            chunked.Process(input.AsSpan(at, size), pieces.AsSpan(at, size));
            at += size;
        }

        pieces.Should().Equal(oneCall);
        chunked.SamplesProcessed.Should().Be(input.Length);
    }

    [Fact]
    public void Same_Seed_Repeats_And_Another_Seed_Differs()
    {
        float[] input = BurstsAndGaps(seed: 3, bursts: 2, burstSamples: Rate, gapSamples: Rate);
        float[] a = Full(seed: 5).Process(input);
        float[] b = Full(seed: 5).Process(input);
        float[] c = Full(seed: 6).Process(input);
        b.Should().Equal(a);
        c.Should().NotEqual(a);
    }

    [Fact]
    public void Fading_Runs_On_Through_The_Silence_Between_Calls()
    {
        // A tone burst, silence, a second burst, each its own call, noiseless through a
        // single fading path: the second burst sees the fade where the process has got to,
        // not a fresh draw. Its received envelope matches a single long call exactly, and
        // the fade a gap of 0.1 s apart is strongly correlated across the call boundary.
        var path = new WattersonPath(0, Fading: true, DopplerSpreadHz: 0.2);
        int burst = Rate * 2;
        int gap = Rate / 10;
        var tone = new float[burst];
        for (int i = 0; i < burst; i++)
        {
            tone[i] = (float)Math.Cos(2 * Math.PI * 1800 * i / Rate);
        }

        var split = new ContinuousWattersonChannel(Rate, 8, double.PositiveInfinity, 0, path);
        float[] first = split.Process(tone);
        split.Process(new float[gap]);
        float[] second = split.Process(tone);

        var joined = new float[(2 * burst) + gap];
        tone.CopyTo(joined, 0);
        tone.CopyTo(joined, burst + gap);
        float[] once = new ContinuousWattersonChannel(Rate, 8, double.PositiveInfinity, 0, path).Process(joined);
        once.AsSpan(burst + gap, burst).ToArray().Should().Equal(second);

        // Envelope power over the last 0.1 s of the first burst and the first 0.1 s of the
        // second (after the channel latency): 0.2 Hz fading barely moves in 0.2 s.
        int latency = split.LatencySamples;
        int window = Rate / 10;
        double Power(float[] x, int from)
        {
            double sum = 0;
            for (int i = from; i < from + window; i++)
            {
                sum += (double)x[i] * x[i];
            }

            return sum / window;
        }

        double end = Power(first, burst - window);
        double start = Power(second, latency);
        (10 * Math.Log10(start / end)).Should().BeInRange(-3, 3);
    }

    [Fact]
    public void Noise_Floor_Is_Set_By_The_Given_Signal_Power_In_Bursts_And_Silence()
    {
        // AWGN at 10 dB in 3 kHz against a fixed reference power: the silence carries the
        // same noise as the burst, at the calibrated level.
        const double snrDb = 10;
        const double power = 0.045; // a 0.3 amplitude tone
        var channel = new ContinuousWattersonChannel(Rate, 17, snrDb, power);
        channel.LatencySamples.Should().Be(0);
        float[] silence = channel.Process(new float[Rate * 20]);

        double measured = 0;
        foreach (float s in silence)
        {
            measured += (double)s * s;
        }

        measured /= silence.Length;
        double expected = power / Math.Pow(10, snrDb / 10) * (Rate / 2.0) / 3000;
        (10 * Math.Log10(measured / expected)).Should().BeInRange(-0.1, 0.1);
    }

    [Fact]
    public void Noiseless_Direct_Path_Passes_The_Input_Unchanged()
    {
        float[] input = BurstsAndGaps(seed: 7, bursts: 1, burstSamples: Rate, gapSamples: 10);
        var channel = new ContinuousWattersonChannel(Rate, 1, double.PositiveInfinity, 0);
        channel.Process(input).Should().Equal(input);
    }

    [Fact]
    public void Paths_Land_At_The_Latency_Plus_Their_Delays()
    {
        // The WID 2 static rig (0, 3, 9 ms), noiseless. Correlate against the input and its
        // quadrature twin, so a path's random static phase does not hide it.
        var random = new Random(3);
        var input = new float[Rate * 4];
        var quadrature = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            double r = (random.NextDouble() - 0.5) * 2;
            double theta = 2 * Math.PI * 1800 * i / Rate;
            input[i] = (float)(r * Math.Sin(theta));
            quadrature[i] = (float)(r * Math.Cos(theta));
        }

        var channel = new ContinuousWattersonChannel(
            Rate, 5, double.PositiveInfinity, 0, new WattersonPath(0), new WattersonPath(3.0), new WattersonPath(9.0));
        float[] output = channel.Process(input);
        int latency = channel.LatencySamples;
        latency.Should().Be((129 / 2) + 15);

        double Xcorr(int lag)
        {
            double i = 0, q = 0;
            for (int n = 0; n < input.Length - lag; n++)
            {
                i += (double)output[n + lag] * input[n];
                q += (double)output[n + lag] * quadrature[n];
            }

            return Math.Sqrt((i * i) + (q * q));
        }

        double reference = Xcorr(latency);
        foreach (double delayMs in new[] { 3.0, 9.0 })
        {
            int expected = latency + (int)Math.Round(delayMs * Rate / 1000);
            double best = 0;
            for (int lag = expected - 2; lag <= expected + 2; lag++)
            {
                best = Math.Max(best, Xcorr(lag));
            }

            (best / reference).Should().BeGreaterThan(0.7, $"an equal-power echo must appear at {delayMs} ms");
        }

        (Xcorr(latency + (int)Math.Round(6.0 * Rate / 1000)) / reference).Should().BeLessThan(0.25);
        (Xcorr(latency - 10) / reference).Should().BeLessThan(0.25, "nothing arrives before the latency");
    }

    [Fact]
    public void Poor_Keeps_The_Mean_Power()
    {
        // Equal-power paths normalised: over minutes of Poor fading the received power
        // averages to the transmitted power.
        float[] input = BurstsAndGaps(seed: 11, bursts: 1, burstSamples: Rate * 240, gapSamples: 0);
        float[] output = new ContinuousWattersonChannel(Rate, 12, double.PositiveInfinity, 0, WattersonChannel.Poor)
            .Process(input);
        double pIn = 0, pOut = 0;
        for (int i = 0; i < input.Length; i++)
        {
            pIn += (double)input[i] * input[i];
            pOut += (double)output[i] * output[i];
        }

        (10 * Math.Log10(pOut / pIn)).Should().BeInRange(-1.5, 1.5);
    }

    [Fact]
    public void Transmit_And_Receive_Passbands_Each_Cut_Once()
    {
        // A tone at the shared 2600 Hz edge loses 6 dB in each radio: 12 dB in all.
        var tone = new float[Rate * 2];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * 2600 * i / Rate);
        }

        var channel = new ContinuousWattersonChannel(Rate, 1, double.PositiveInfinity, 0)
        {
            TransmitPassband = new SsbPassband(),
            ReceivePassband = new SsbPassband(),
        };
        float[] output = channel.Process(tone);
        int latency = channel.LatencySamples;
        latency.Should().Be(2 * new SsbFilter(Rate, new SsbPassband()).LatencySamples);

        double pIn = 0, pOut = 0;
        for (int i = 2 * latency; i < tone.Length; i++)
        {
            pIn += (double)tone[i] * tone[i];
            pOut += (double)output[i] * output[i];
        }

        (10 * Math.Log10(pOut / pIn)).Should().BeApproximately(-12.04, 0.2);
    }

    [Fact]
    public void Receive_Passband_Shapes_The_Noise_Too()
    {
        // Noise enters at the receiver's antenna, before its filter: through a 2.2 kHz
        // passband, white noise over the 4.8 kHz Nyquist band keeps the share of its power
        // the filter's response passes, about 2200/4800.
        var channel = new ContinuousWattersonChannel(Rate, 3, 0, 1) { ReceivePassband = new SsbPassband(400, 2600) };
        float[] noise = channel.Process(new float[Rate * 20]);
        double measured = 0;
        foreach (float s in noise)
        {
            measured += (double)s * s;
        }

        measured /= noise.Length;
        double white = 1.0 * (Rate / 2.0) / 3000;
        var filter = new SsbFilter(Rate, new SsbPassband(400, 2600));
        double kept = 0;
        for (int hz = 0; hz < Rate / 2; hz++)
        {
            kept += Math.Pow(10, filter.ResponseDb(hz + 0.5) / 10);
        }

        kept /= Rate / 2.0;
        kept.Should().BeApproximately(2200.0 / 4800, 0.02);
        (measured / white / kept).Should().BeApproximately(1, 0.02);
    }

    [Fact]
    public void Moderate_Is_Two_Equal_Rayleigh_Paths_1_ms_Apart_With_Half_A_Hertz_Spread()
    {
        WattersonChannel.Moderate.Should().Equal(
            new WattersonPath(0, Fading: true, DopplerSpreadHz: 0.5),
            new WattersonPath(1, Fading: true, DopplerSpreadHz: 0.5));
    }

    [Fact]
    public void Rejects_A_Finite_Snr_Without_A_Signal_Power()
    {
        Action act = () => _ = new ContinuousWattersonChannel(Rate, 1, 10, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
