# M0LTE.HfChannel

An HF channel simulator for measuring modems in .NET, extracted from the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. It is the HF counterpart of [M0LTE.FmChannel](https://github.com/M0LTE/M0LTE.FmChannel): every HF modem measured through it, in pdn-soundmodem or in a modem's own package, shares one definition of the channel.

| Type | What it is |
| --- | --- |
| `WattersonChannel` | The Watterson model of MIL-STD-188-110D Appendix E: a tapped delay line on the complex envelope, each path static or Rayleigh-fading with a Gaussian Doppler spectrum, plus AWGN calibrated as SNR in a 3 kHz noise bandwidth. Equal-power paths, total power normalised. |
| `WattersonPath` | One path: delay, fading or static, Doppler spread and shift. |
| `WattersonChannel.Poor` | The ITU-R F.1487 "Poor" geometry (two Rayleigh paths 2 ms apart, 1 Hz spread) used by the 110D performance masks. |
| `ImpulseNoise`, `ImpulseNoiseProfile` | Static crashes, calibrated from a measured 40 m capture campaign: arrival rate, amplitude and duration distributions. |

- Targets `net10.0`. Depends on [M0LTE.Ofdm](https://www.nuget.org/packages/M0LTE.Ofdm) for its complex type `Cf`.
- The public API is locked by a test and the package follows [Semantic Versioning](https://semver.org/); see [docs/versioning.md](docs/versioning.md).

## Install

```sh
dotnet add package M0LTE.HfChannel
```

## Quick start

```csharp
using M0LTE.HfChannel;

// A signal at 9600 Hz, centred on 1800 Hz, through the Poor channel at 10 dB SNR.
var channel = new WattersonChannel(sampleRate: 9600, seed: 1, WattersonChannel.Poor);
float[] received = channel.Apply(transmitted, snrDb: 10);

// The same, with an evening's worth of static crashes on top.
float[] noisy = channel.Apply(transmitted, snrDb: 10, impulses: ImpulseNoiseProfile.Evening);
```

Set `CentreHz` when the signal under test is not centred on 1800 Hz: the channel forms the complex envelope about that centre and low-passes it at 2.2 kHz, so a signal elsewhere would be cut.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE) and [PROVENANCE.md](PROVENANCE.md).
