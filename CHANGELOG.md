# Changelog

All notable changes to `M0LTE.HfChannel`. Versions follow [docs/versioning.md](docs/versioning.md).

## 0.2.0

Additive: existing code keeps compiling, and `WattersonChannel.Apply` gives the same output as before for the same seed.

- `ContinuousWattersonChannel`, a streaming form of `WattersonChannel`. Fading, noise and filter state carry across calls, so bursts and the silence between them pass through one continuous fading process, and the output depends only on the seed and the input samples, not on how the input is split into calls. The noise is set against a fixed signal power given at construction. Each path's fading uses the batch channel's generator, sample for sample (a given seed gives a different realisation from `Apply`, since each path and the noise draw from their own streams). The output lags the input by `LatencySamples`.
- `SsbPassband` and `SsbFilter`: an SSB radio's audio passband defined by its -6 dB edges, default 400 to 2600 Hz (the FT-818 and FT-450 published transmit response), as a linear-phase FIR band-pass. `ContinuousWattersonChannel` takes one for the sending radio (`TransmitPassband`, before the channel) and one for the receiving radio (`ReceivePassband`, after the noise).
- `WattersonChannel.Moderate`: the ITU-R F.1487 (05/2000) Annex 3 section 3.2 mid-latitude Moderate geometry (the former F.520 moderate channel), two equal Rayleigh paths 1 ms apart with 0.5 Hz spread.

## 0.1.0

First release: `WattersonChannel`, `WattersonPath`, `WattersonChannel.Poor`, `ImpulseNoise` and `ImpulseNoiseProfile`, moved from pdn-soundmodem.
