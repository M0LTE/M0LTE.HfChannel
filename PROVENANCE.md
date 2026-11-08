# Provenance

`M0LTE.HfChannel` is the HF channel simulator from the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) test project, moved here unchanged apart from its namespace at pdn-soundmodem commit `67941730e5e896b12d2f5f63f7109408f5f344ac` (2026-10-22). It was written for that project and carries its licence, AGPL-3.0-or-later.

| File | Provenance |
| --- | --- |
| `WattersonChannel.cs` | Original code following the Watterson model as MIL-STD-188-110D (29 Dec 2017, Distribution A) Appendix E specifies it: tapped delay line on the complex envelope (E.5.1), independent complex-Gaussian tap gains filtered to a Gaussian Doppler spectrum (E.5.3, E.5.4), AWGN as SNR in a 3 kHz bandwidth. The "Poor" geometry is ITU-R F.1487 Mid-Latitude Disturbed; the "Moderate" preset (0.2.0) is F.1487 (05/2000) Annex 3 section 3.2, Mid-Latitude Moderate (1 ms, 0.5 Hz two-sigma spread, two equal independently fading paths), checked against the published Recommendation. No other implementation was consulted. |
| `ImpulseNoise.cs` | Original code. The model's parameters come from pdn-soundmodem's own 40 m capture campaign (its `docs/dev/archive/bench/impulse-stats-2026-08-06`) and were verified by re-analysing the injector's output with the same instrument (`docs/dev/archive/bench/impulse-model-validation-2026-08-07.txt` in that repository). |
| `ContinuousWattersonChannel.cs` | Original code (0.2.0): `WattersonChannel`'s model restated as a causal stream, with the same envelope filter, fractional delay and fading process. |
| `SsbPassband.cs` | Original code (0.2.0): a Blackman-windowed FIR band-pass. The default edges are the published transmit audio response of the Yaesu FT-818ND and FT-450 operating manuals ("400 Hz-2600 Hz (-6 dB)"). |
