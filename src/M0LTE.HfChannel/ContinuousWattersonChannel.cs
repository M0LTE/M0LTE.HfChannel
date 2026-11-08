using M0LTE.Ofdm;

namespace M0LTE.HfChannel;

/// <summary>
/// The streaming form of <see cref="WattersonChannel"/>: the same Watterson model (tapped
/// delay line on the complex envelope, Rayleigh-fading or static paths with a Gaussian
/// Doppler spectrum, AWGN as SNR in a 3 kHz noise bandwidth), as one continuous realisation
/// that carries its fading state, noise and filter state across calls. Feed it everything
/// that goes on the air, bursts and the silence between them, in calls of any size: bursts
/// then pass through one fading process, and the output depends only on the seed and the
/// input samples, never on how the input was split into calls.
/// </summary>
/// <remarks>
/// <para>
/// Differences from <see cref="WattersonChannel.Apply"/>, which makes each call its own
/// realisation:
/// </para>
/// <list type="bullet">
/// <item>The noise is calibrated against a fixed <c>signalPower</c> given at construction
/// (the mean power of a transmission at the channel input), not against each call's input,
/// so silence carries the same noise floor as a burst. With a
/// <see cref="TransmitPassband"/>, the SNR still refers to that input power, so the
/// filter's loss counts against the signal; to set the SNR of the filtered (on-air)
/// signal instead, pass a burst's mean power after an <see cref="SsbFilter"/> of the same
/// passband.</item>
/// <item>It is causal: the output lags the input by <see cref="LatencySamples"/>.</item>
/// <item>No impulse noise and no gain recording.</item>
/// </list>
/// <para>
/// Signal path: transmit passband (optional), channel, noise, receive passband (optional).
/// The noise is added at the receiver's input (the antenna), so the receive passband shapes
/// it as it shapes the signal; its level is set from <c>signalPower</c> as above. With no paths and no
/// frequency offset the channel is the ideal direct path (passband passthrough plus noise),
/// as in <see cref="WattersonChannel.Apply"/>.
/// </para>
/// <para>
/// Each path's fading process is the one <see cref="WattersonChannel"/> uses, sample for
/// sample: the same low-rate filtered Gaussian process, linearly interpolated, so the
/// statistics of the Poor and other presets are unchanged. The noise and each path draw
/// from their own random streams, all derived from the seed.
/// </para>
/// </remarks>
public sealed class ContinuousWattersonChannel
{
    private const int FractionalDelayHalf = 16;
    private const int FractionalDelayLookahead = FractionalDelayHalf - 1;

    private readonly int _sampleRate;
    private readonly WattersonPath[] _paths;
    private readonly int _seed;
    private readonly double _snrDb;
    private readonly double _signalPower;
    private State? _state;

    /// <summary>Creates the channel.</summary>
    /// <param name="sampleRate">Sample rate of the audio, Hz.</param>
    /// <param name="seed">Seeds every random draw (noise, fading, static path phases).</param>
    /// <param name="snrDb">Signal-to-noise ratio, with the noise measured in
    /// <see cref="NoiseBandwidthHz"/>; <see cref="double.PositiveInfinity"/> for no noise.</param>
    /// <param name="signalPower">Mean power (mean square) of a transmission as it enters the
    /// channel, against which <paramref name="snrDb"/> sets the noise.</param>
    /// <param name="paths">The propagation paths; none gives the ideal direct path.</param>
    public ContinuousWattersonChannel(
        int sampleRate, int seed, double snrDb, double signalPower, params WattersonPath[] paths)
    {
        if (sampleRate < WattersonChannel.GainSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), $"must be at least {WattersonChannel.GainSampleRateHz} Hz");
        }

        if (!double.IsPositiveInfinity(snrDb) && !(signalPower > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(signalPower), "must be positive when the SNR is finite");
        }

        ArgumentNullException.ThrowIfNull(paths);
        _sampleRate = sampleRate;
        _seed = seed;
        _snrDb = snrDb;
        _signalPower = signalPower;
        _paths = (WattersonPath[])paths.Clone();
    }

    /// <summary>The audio centre the complex envelope is formed about (1800 Hz unless set),
    /// as <see cref="WattersonChannel.CentreHz"/>.</summary>
    public double CentreHz { get; init; } = 1800;

    /// <summary>The bandwidth the SNR's noise is measured in, Hz (3000 unless set, per
    /// MIL-STD-188-110D D.6.2).</summary>
    public double NoiseBandwidthHz { get; init; } = 3000;

    /// <summary>A constant carrier frequency offset, Hz (the D.6.4 rig). 0 unless set.</summary>
    public double FrequencyOffsetHz { get; init; }

    /// <summary>The sending radio's audio passband, applied before the channel; null (the
    /// default) for none.</summary>
    public SsbPassband? TransmitPassband { get; init; }

    /// <summary>The receiving radio's audio passband, applied after the noise; null (the
    /// default) for none.</summary>
    public SsbPassband? ReceivePassband { get; init; }

    /// <summary>The delay from input to output, in samples: the passband filters' delays,
    /// plus, unless the channel is the ideal direct path, the envelope filter's and the
    /// path delay line's.</summary>
    public int LatencySamples => Built.Latency;

    /// <summary>How many samples have passed through the channel so far.</summary>
    public long SamplesProcessed => _state?.Position ?? 0;

    /// <summary>Passes the next part of the stream through the channel. Returns as many
    /// samples as were given.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        Process(input, output);
        return output;
    }

    /// <summary>Passes the next part of the stream through the channel into
    /// <paramref name="output"/>, which must be at least as long as
    /// <paramref name="input"/> (it may be the same memory).</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("output is shorter than input", nameof(output));
        }

        State s = Built;
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = s.Step(input[i]);
        }
    }

    private State Built => _state ??= new State(this);

    /// <summary>The per-stream state, built on first use so the init-only properties are set.</summary>
    private sealed class State
    {
        private readonly int _sampleRate;
        private readonly double _centreHz;
        private readonly double _offsetHz;
        private readonly bool _direct;
        private readonly SsbFilter? _transmit;
        private readonly SsbFilter? _receive;
        private readonly double _noiseSigma;
        private readonly Random _noiseRandom;

        // Envelope low-pass (the batch channel's ToEnvelope filter, run causally).
        private readonly float[] _envelopeTaps = [];
        private readonly Cf[] _mixed = [];
        private int _mixedHead;

        // Delay line of envelope samples, newest at _envHead.
        private readonly Cf[] _envelope = [];
        private int _envHead;

        private readonly PathState[] _pathStates = [];
        private readonly float _pathScale;
        private readonly int _channelLatency;

        public State(ContinuousWattersonChannel owner)
        {
            _sampleRate = owner._sampleRate;
            _centreHz = owner.CentreHz;
            _offsetHz = owner.FrequencyOffsetHz;
            _direct = owner._paths.Length == 0 && _offsetHz == 0;

            if (owner.TransmitPassband is not null)
            {
                _transmit = new SsbFilter(_sampleRate, owner.TransmitPassband);
            }

            if (owner.ReceivePassband is not null)
            {
                _receive = new SsbFilter(_sampleRate, owner.ReceivePassband);
            }

            // Every component draws from its own stream, derived from the seed in a fixed
            // order: noise first, then one per path.
            var master = new Random(owner._seed);
            _noiseRandom = new Random(master.Next());

            if (double.IsPositiveInfinity(owner._snrDb))
            {
                _noiseSigma = 0;
            }
            else
            {
                double snr = Math.Pow(10, owner._snrDb / 10.0);
                double noisePower = owner._signalPower / snr * (_sampleRate / 2.0) / owner.NoiseBandwidthHz;
                _noiseSigma = Math.Sqrt(noisePower);
            }

            if (!_direct)
            {
                _envelopeTaps = EnvelopeTaps(_sampleRate);
                _mixed = new Cf[_envelopeTaps.Length];

                WattersonPath[] paths = owner._paths.Length == 0 ? [new WattersonPath(0)] : owner._paths;
                _pathScale = (float)(1.0 / Math.Sqrt(paths.Length));
                _pathStates = new PathState[paths.Length];
                int longest = 0;
                for (int p = 0; p < paths.Length; p++)
                {
                    _pathStates[p] = new PathState(paths[p], _sampleRate, new Random(master.Next()));
                    longest = Math.Max(longest, _pathStates[p].Whole);
                }

                // Room for the longest delay plus the fractional-delay kernel's reach.
                _envelope = new Cf[longest + (2 * FractionalDelayHalf) + 1];
                _channelLatency = (_envelopeTaps.Length / 2) + FractionalDelayLookahead;
            }

            Latency = (_transmit?.LatencySamples ?? 0) + _channelLatency + (_receive?.LatencySamples ?? 0);
        }

        public int Latency { get; }

        public long Position { get; private set; }

        public float Step(float x)
        {
            long n = Position++;
            if (_transmit is not null)
            {
                x = _transmit.Step(x);
            }

            float y = _direct ? x : Channel(x, n);
            if (_noiseSigma > 0)
            {
                y += (float)(_noiseSigma * Gaussian.Next(_noiseRandom));
            }

            if (_receive is not null)
            {
                y = _receive.Step(y);
            }

            return y;
        }

        private float Channel(float x, long n)
        {
            // Mix down about the centre and low-pass: gain 2 restores the envelope amplitude.
            double theta = 2.0 * Math.PI * _centreHz * n / _sampleRate;
            _mixedHead = _mixedHead == 0 ? _mixed.Length - 1 : _mixedHead - 1;
            _mixed[_mixedHead] = new Cf((float)(x * Math.Cos(theta)), (float)(-x * Math.Sin(theta)));
            var acc = Cf.Zero;
            int k = _mixedHead;
            for (int j = 0; j < _envelopeTaps.Length; j++)
            {
                acc += _mixed[k] * _envelopeTaps[j];
                if (++k == _mixed.Length)
                {
                    k = 0;
                }
            }

            _envHead = _envHead == 0 ? _envelope.Length - 1 : _envHead - 1;
            _envelope[_envHead] = acc * 2f;

            // The envelope now in the delay line belongs to input time n - envelope delay;
            // each path reads it a further lookahead plus its own delay back.
            long t = n - _channelLatency;
            var summed = Cf.Zero;
            foreach (PathState path in _pathStates)
            {
                Cf delayed = path.Read(_envelope, _envHead);
                if (path.ShiftHz != 0)
                {
                    delayed *= Cf.Cmplx((float)(2.0 * Math.PI * path.ShiftHz * t / _sampleRate));
                }

                summed += delayed * path.NextGain() * _pathScale;
            }

            if (_offsetHz != 0)
            {
                summed *= Cf.Cmplx((float)(2.0 * Math.PI * _offsetHz * t / _sampleRate));
            }

            // Back to a real passband signal, on the carrier phase of the input it came from.
            double phi = 2.0 * Math.PI * _centreHz * t / _sampleRate;
            return (float)((summed.Re * Math.Cos(phi)) - (summed.Im * Math.Sin(phi)));
        }

        private static float[] EnvelopeTaps(int sampleRate)
        {
            // The batch channel's envelope filter: windowed sinc, cutoff 2.2 kHz, 129 taps
            // at 9600 Hz scaled with the sample rate, normalised to unity DC gain.
            int taps = (129 * sampleRate / 9600) | 1;
            int half = taps / 2;
            var h = new double[taps];
            double sum = 0;
            double cutoff = 2200.0 / sampleRate;
            for (int i = 0; i < taps; i++)
            {
                double x = i - half;
                double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                double window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)));
                h[i] = sinc * window;
                sum += h[i];
            }

            var result = new float[taps];
            for (int i = 0; i < taps; i++)
            {
                result[i] = (float)(h[i] / sum);
            }

            return result;
        }
    }

    private sealed class PathState
    {
        private readonly int _offset; // FractionalDelayLookahead + whole delay
        private readonly float[]? _kernel; // null for a whole-sample delay
        private readonly FadingProcess? _fading;
        private readonly Cf _staticGain;

        public PathState(WattersonPath path, int sampleRate, Random random)
        {
            double delay = path.DelayMs * sampleRate / 1000.0;
            if (delay < 0)
            {
                throw new ArgumentException("path delays must not be negative", nameof(path));
            }

            Whole = (int)Math.Floor(delay);
            double frac = delay - Whole;
            _offset = FractionalDelayLookahead + Whole;
            if (frac != 0)
            {
                // The batch channel's windowed-sinc fractional delay: tap j (from
                // -half + 1 to half) weights the sample j later than the whole delay.
                _kernel = new float[2 * FractionalDelayHalf];
                for (int j = -FractionalDelayHalf + 1; j <= FractionalDelayHalf; j++)
                {
                    double u = j - frac;
                    double w = Math.Abs(u) < 1e-9
                        ? 1.0
                        : Math.Sin(Math.PI * u) / (Math.PI * u)
                            * (0.5 + (0.5 * Math.Cos(Math.PI * u / FractionalDelayHalf)));
                    _kernel[j + FractionalDelayHalf - 1] = (float)w;
                }
            }

            ShiftHz = path.DopplerShiftHz;
            if (path.Fading)
            {
                _fading = new FadingProcess(random, path.DopplerSpreadHz / 2.0, sampleRate);
            }
            else
            {
                _staticGain = Cf.Cmplx((float)(random.NextDouble() * 2 * Math.PI));
            }
        }

        public int Whole { get; }

        public double ShiftHz { get; }

        public Cf NextGain() => _fading?.Next() ?? _staticGain;

        public Cf Read(Cf[] line, int head)
        {
            int length = line.Length;
            if (_kernel is null)
            {
                return line[(head + _offset) % length];
            }

            // Output = sum over j of line[t - whole - j] * kernel(j), t = newest - lookahead.
            var acc = Cf.Zero;
            for (int j = -FractionalDelayHalf + 1; j <= FractionalDelayHalf; j++)
            {
                int back = _offset + j;
                acc += line[(head + back) % length] * _kernel[j + FractionalDelayHalf - 1];
            }

            return acc;
        }
    }
}

/// <summary>
/// One path's fading gain as a stream: the process <see cref="WattersonChannel"/> generates
/// in one go (white complex Gaussian at the 96 Hz gain rate, filtered to a Gaussian Doppler
/// spectrum, linearly interpolated to the sample rate), produced one sample at a time with
/// the same arithmetic in the same order, so for the same random stream it gives the same
/// gains, bit for bit.
/// </summary>
internal sealed class FadingProcess
{
    private readonly Random _random;
    private readonly float[] _filter; // filter[j] * norm, as the batch channel multiplies
    private readonly Cf[] _white; // circular, oldest at _whiteStart
    private readonly int _decimation;
    private int _whiteStart;
    private Cf _low0;
    private Cf _low1;
    private long _index;

    public FadingProcess(Random random, double sigmaHz, int sampleRate)
    {
        _random = random;
        const int gainRate = WattersonChannel.GainSampleRateHz;
        double sigmaT = 1.0 / (2.0 * Math.PI * sigmaHz * Math.Sqrt(2.0));
        int half = (int)Math.Ceiling(4 * sigmaT * gainRate);
        var filter = new double[(2 * half) + 1];
        double energy = 0;
        for (int i = 0; i < filter.Length; i++)
        {
            double t = (i - half) / (double)gainRate;
            filter[i] = Math.Exp(-4.0 * Math.PI * Math.PI * sigmaHz * sigmaHz * t * t);
            energy += filter[i] * filter[i];
        }

        double norm = 1.0 / Math.Sqrt(energy);
        _filter = new float[filter.Length];
        for (int i = 0; i < filter.Length; i++)
        {
            _filter[i] = (float)(filter[i] * norm);
        }

        _decimation = sampleRate / gainRate;
        _white = new Cf[_filter.Length];
        for (int i = 0; i < _white.Length; i++)
        {
            _white[i] = DrawWhite();
        }

        _low0 = Convolve();
        Advance();
        _low1 = Convolve();
    }

    public Cf Next()
    {
        long i0 = _index / _decimation;
        float frac = (_index - (i0 * _decimation)) / (float)_decimation;
        Cf gain = (_low0 * (1 - frac)) + (_low1 * frac);
        if (++_index % _decimation == 0)
        {
            _low0 = _low1;
            Advance();
            _low1 = Convolve();
        }

        return gain;
    }

    private void Advance()
    {
        _white[_whiteStart] = DrawWhite();
        _whiteStart = (_whiteStart + 1) % _white.Length;
    }

    private Cf Convolve()
    {
        var acc = Cf.Zero;
        int k = _whiteStart;
        for (int j = 0; j < _filter.Length; j++)
        {
            acc += _white[k] * _filter[j];
            if (++k == _white.Length)
            {
                k = 0;
            }
        }

        return acc;
    }

    private Cf DrawWhite() =>
        new((float)(Gaussian.Next(_random) / Math.Sqrt(2)), (float)(Gaussian.Next(_random) / Math.Sqrt(2)));
}

/// <summary>The Box-Muller draw <see cref="WattersonChannel"/> uses.</summary>
internal static class Gaussian
{
    public static double Next(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
