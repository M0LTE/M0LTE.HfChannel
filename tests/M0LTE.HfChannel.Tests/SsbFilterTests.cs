namespace M0LTE.HfChannel.Tests;

public class SsbFilterTests
{
    private const int Rate = 9600;

    /// <summary>Gain of a steady tone through a fresh filter, in dB, measured on the output
    /// after the start-up transient.</summary>
    private static double MeasuredGainDb(SsbPassband passband, double hz, int rate = Rate)
    {
        var filter = new SsbFilter(rate, passband);
        var input = new float[rate * 2];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Sin(2 * Math.PI * hz * i / rate);
        }

        float[] output = filter.Process(input);
        int skip = 2 * filter.LatencySamples;
        double pIn = 0, pOut = 0;
        for (int i = skip; i < input.Length; i++)
        {
            pIn += (double)input[i - filter.LatencySamples] * input[i - filter.LatencySamples];
            pOut += (double)output[i] * output[i];
        }

        return 10 * Math.Log10(pOut / pIn);
    }

    /// <summary>The frequency between <paramref name="inside"/> (gain above -6 dB) and
    /// <paramref name="outside"/> (below) where the measured gain crosses -6 dB.</summary>
    private static double MeasuredEdge(SsbPassband passband, double inside, double outside)
    {
        for (int i = 0; i < 20; i++)
        {
            double mid = (inside + outside) / 2;
            if (MeasuredGainDb(passband, mid) > -6.0206)
            {
                inside = mid;
            }
            else
            {
                outside = mid;
            }
        }

        return (inside + outside) / 2;
    }

    [Fact]
    public void Default_Passband_Is_The_Bench_Radios_Transmit_Response()
    {
        var passband = new SsbPassband();
        passband.LowHz.Should().Be(400);
        passband.HighHz.Should().Be(2600);
    }

    [Theory]
    [InlineData(400, 2600)]
    [InlineData(300, 2400)]
    [InlineData(500, 2700)]
    public void Measured_Minus_6_dB_Edges_Are_Where_The_Passband_Says(double low, double high)
    {
        var passband = new SsbPassband(low, high);
        MeasuredEdge(passband, inside: low + 200, outside: low - 200).Should().BeApproximately(low, 5);
        MeasuredEdge(passband, inside: high - 200, outside: high + 200).Should().BeApproximately(high, 5);
    }

    [Fact]
    public void Passband_Is_Flat_And_Stopband_Is_Deep()
    {
        var passband = new SsbPassband();
        foreach (double hz in new[] { 700.0, 1000, 1500, 1800, 2200 })
        {
            MeasuredGainDb(passband, hz).Should().BeInRange(-0.1, 0.1, $"{hz} Hz is in the passband");
        }

        foreach (double hz in new[] { 100.0, 3000, 3600 })
        {
            MeasuredGainDb(passband, hz).Should().BeLessThan(-60, $"{hz} Hz is beyond the skirts");
        }
    }

    [Fact]
    public void Response_Db_Agrees_With_Measurement()
    {
        var passband = new SsbPassband();
        var filter = new SsbFilter(Rate, passband);
        filter.ResponseDb(400).Should().BeApproximately(-6.02, 0.1);
        filter.ResponseDb(2600).Should().BeApproximately(-6.02, 0.1);
        foreach (double hz in new[] { 350.0, 1500, 2650 })
        {
            filter.ResponseDb(hz).Should().BeApproximately(MeasuredGainDb(passband, hz), 0.1);
        }
    }

    [Fact]
    public void Edges_Hold_At_48_kHz()
    {
        var filter = new SsbFilter(48000, new SsbPassband());
        filter.ResponseDb(400).Should().BeApproximately(-6.02, 0.1);
        filter.ResponseDb(2600).Should().BeApproximately(-6.02, 0.1);
        filter.ResponseDb(1500).Should().BeApproximately(0, 0.05);
    }

    [Fact]
    public void Streaming_Is_Independent_Of_Call_Sizes_And_Matches_Apply_Delayed()
    {
        var random = new Random(1);
        var input = new float[Rate * 3];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)(random.NextDouble() - 0.5);
        }

        var whole = new SsbFilter(Rate, new SsbPassband());
        float[] oneCall = whole.Process(input);

        var chunked = new SsbFilter(Rate, new SsbPassband());
        var pieces = new float[input.Length];
        int at = 0;
        while (at < input.Length)
        {
            int size = Math.Min(random.Next(1, 700), input.Length - at);
            chunked.Process(input.AsSpan(at, size), pieces.AsSpan(at, size));
            at += size;
        }

        pieces.Should().Equal(oneCall);

        float[] aligned = whole.Apply(input);
        int latency = whole.LatencySamples;
        for (int i = 0; i < input.Length - latency; i++)
        {
            oneCall[i + latency].Should().Be(aligned[i]);
        }
    }

    [Theory]
    [InlineData(0, 2600)]
    [InlineData(2600, 400)]
    [InlineData(400, 4800)]
    public void Rejects_Impossible_Edges(double low, double high)
    {
        Action act = () => _ = new SsbFilter(Rate, new SsbPassband(low, high));
        act.Should().Throw<ArgumentException>();
    }
}
