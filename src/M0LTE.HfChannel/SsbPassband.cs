namespace M0LTE.HfChannel;

/// <summary>The audio passband of an amateur SSB radio path, defined by its -6 dB edges.</summary>
/// <param name="LowHz">Lower -6 dB edge in Hz.</param>
/// <param name="HighHz">Upper -6 dB edge in Hz.</param>
/// <remarks>
/// The defaults, 400 to 2600 Hz, are the published transmit audio response of the Yaesu
/// FT-818ND and FT-450 ("400 Hz-2600 Hz (-6 dB)"). Receive filters are specified only by
/// their width (the FT-818's is 2.2 kHz at -6 dB), not by where the edges sit in the audio,
/// so give the edges you have measured for a receive path rather than relying on the
/// default. The skirts are fixed by <see cref="SsbFilter"/>; a measured response may
/// replace this shape later.
/// </remarks>
public sealed record SsbPassband(double LowHz = 400, double HighHz = 2600);

/// <summary>
/// A linear-phase FIR band-pass filter modelling one SSB radio's audio passband: a
/// Blackman-windowed difference of two sinc low-passes, which puts the response at exactly
/// half amplitude (-6 dB) at each <see cref="SsbPassband"/> edge, unity gain in the middle,
/// and at least 70 dB of rejection beyond the skirts. Each skirt falls from the passband to
/// the stopband over about <see cref="TransitionHz"/>, centred on its edge. No randomness:
/// the output depends only on the input.
/// </summary>
/// <remarks>
/// Use <see cref="Process(ReadOnlySpan{float})"/> on a stream: it is causal, keeps its state
/// between calls (so splitting the input into calls of any size gives the same output), and
/// delays the signal by <see cref="LatencySamples"/>. Use <see cref="Apply"/> on a whole
/// buffer: it compensates the delay, so the output lines up with the input.
/// </remarks>
public sealed class SsbFilter
{
    /// <summary>The nominal width of each skirt, from the passband to the stopband, Hz.</summary>
    public const double TransitionHz = 300;

    private readonly float[] _taps;
    private readonly float[] _history;
    private int _head;

    /// <summary>Creates the filter for <paramref name="passband"/> at
    /// <paramref name="sampleRate"/>.</summary>
    public SsbFilter(int sampleRate, SsbPassband passband)
    {
        ArgumentNullException.ThrowIfNull(passband);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "must be positive");
        }

        if (!(passband.LowHz > 0 && passband.LowHz < passband.HighHz && passband.HighHz < sampleRate / 2.0))
        {
            throw new ArgumentException(
                $"edges must satisfy 0 < LowHz < HighHz < {sampleRate / 2.0} Hz (half the sample rate)",
                nameof(passband));
        }

        SampleRate = sampleRate;
        Passband = passband;

        // Blackman: transition (to the stopband) about 5.5 fs / N wide.
        int count = (int)Math.Ceiling(5.5 * sampleRate / TransitionHz) | 1;
        int half = count / 2;
        double low = passband.LowHz / sampleRate;
        double high = passband.HighHz / sampleRate;
        _taps = new float[count];
        for (int i = 0; i < count; i++)
        {
            double x = i - half;
            double ideal = x == 0
                ? 2 * (high - low)
                : (Math.Sin(2 * Math.PI * high * x) - Math.Sin(2 * Math.PI * low * x)) / (Math.PI * x);
            double window = 0.42
                - (0.5 * Math.Cos(2 * Math.PI * i / (count - 1)))
                + (0.08 * Math.Cos(4 * Math.PI * i / (count - 1)));
            _taps[i] = (float)(ideal * window);
        }

        _history = new float[count];
        LatencySamples = half;
    }

    /// <summary>The sample rate the filter was built for.</summary>
    public int SampleRate { get; }

    /// <summary>The passband the filter was built for.</summary>
    public SsbPassband Passband { get; }

    /// <summary>The delay <see cref="Process(ReadOnlySpan{float})"/> adds, in samples (half
    /// the filter length).</summary>
    public int LatencySamples { get; }

    /// <summary>Filters the next part of a stream. The filter's state carries across calls.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        Process(input, output);
        return output;
    }

    /// <summary>Filters the next part of a stream into <paramref name="output"/>, which must
    /// be at least as long as <paramref name="input"/> (it may be the same memory).</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("output is shorter than input", nameof(output));
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = Step(input[i]);
        }
    }

    /// <summary>Filters a whole buffer with no delay: the output has the input's length and
    /// lines up with it. Does not touch the streaming state.</summary>
    public float[] Apply(ReadOnlySpan<float> input)
    {
        int half = LatencySamples;
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            double acc = 0;
            for (int j = 0; j < _taps.Length; j++)
            {
                int k = i + half - j;
                if (k >= 0 && k < input.Length)
                {
                    acc += input[k] * _taps[j];
                }
            }

            output[i] = (float)acc;
        }

        return output;
    }

    /// <summary>The filter's gain at <paramref name="frequencyHz"/>, in dB (0 dB in the
    /// middle of the passband, -6 dB at each edge).</summary>
    public double ResponseDb(double frequencyHz)
    {
        double re = 0, im = 0;
        for (int j = 0; j < _taps.Length; j++)
        {
            double w = 2 * Math.PI * frequencyHz * (j - LatencySamples) / SampleRate;
            re += _taps[j] * Math.Cos(w);
            im -= _taps[j] * Math.Sin(w);
        }

        return 10 * Math.Log10((re * re) + (im * im));
    }

    internal float Step(float x)
    {
        // _history[_head] is the newest sample; tap j multiplies the sample j steps back.
        _head = _head == 0 ? _history.Length - 1 : _head - 1;
        _history[_head] = x;
        double acc = 0;
        int n = _history.Length;
        int k = _head;
        for (int j = 0; j < n; j++)
        {
            acc += _history[k] * _taps[j];
            if (++k == n)
            {
                k = 0;
            }
        }

        return (float)acc;
    }
}
