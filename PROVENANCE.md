# Provenance

`M0LTE.HfChannel` is the HF channel simulator from the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) test project, moved here unchanged apart from its namespace at pdn-soundmodem commit `67941730e5e896b12d2f5f63f7109408f5f344ac` (2026-10-22). It was written for that project and carries its licence, AGPL-3.0-or-later.

| File | Provenance |
| --- | --- |
| `WattersonChannel.cs` | Original code following the Watterson model as MIL-STD-188-110D (29 Dec 2017, Distribution A) Appendix E specifies it: tapped delay line on the complex envelope (E.5.1), independent complex-Gaussian tap gains filtered to a Gaussian Doppler spectrum (E.5.3, E.5.4), AWGN as SNR in a 3 kHz bandwidth. The "Poor" geometry is ITU-R F.1487 Mid-Latitude Disturbed. No other implementation was consulted. |
| `ImpulseNoise.cs` | Original code. The model's parameters come from pdn-soundmodem's own 40 m capture campaign (its `docs/dev/archive/bench/impulse-stats-2026-08-06`) and were verified by re-analysing the injector's output with the same instrument (`docs/dev/archive/bench/impulse-model-validation-2026-08-07.txt` in that repository). |
