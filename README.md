# TrMadenci

TrMadenci is a transparent KAWPOW miner for Windows and NVIDIA/CUDA. The current
production candidate targets Ravencoin, Stratum V1, failover pools, and multiple GPUs.

## Current status

- Configuration model and validation
- Persistent Stratum connections, subscribe/authorize, reconnect and user-pool failover
- Live KAWPOW job parsing, extranonce allocation and share submission
- Persistent randomized developer-fee accounting based only on active hashing time
- Per-program NVRTC-compiled cooperative CUDA search kernel
- Native CUDA device discovery library and test
- Apache-2.0 CPU reference oracle passing all 13 official KAWPOW vectors
- CUDA light-cache upload and batched full-DAG generation
- GPU results matching official vectors from two different generated programs
- CPU verification of every discovered share before submission
- Multi-GPU workers, hashrate/share statistics and clean Ctrl+C shutdown
- Per-GPU temperature, fan, power, utilization, clocks, VRAM and MH/s/W telemetry
- Automated configuration, fee, Stratum serialization and target tests
- ETCHash ECIP-1099 activation/epoch model with distinct DAG-size and seed epochs,
  full CUDA DAG lifecycle, official CPU/GPU nonce vector, Binance ETC job probe, and a
  gated end-to-end session with a CPU-verified Binance-accepted Stratum share

The Binance-first multi-coin expansion order and its support criteria are documented in
[`docs/coin-algorithm-roadmap.md`](docs/coin-algorithm-roadmap.md). New algorithms do
not begin until the production KAWPOW search/share pipeline meets those criteria.

The gated CFX profile has a protocol-only `--probe` path. Conflux/Octopus authenticates
the worker inside `mining.subscribe` rather than using a separate `mining.authorize`
request. The probe parses a live four-field job and calculates exact cache, DAG and
estimated device-memory requirements without allocating a DAG or mining:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --probe --native-probe
```

The managed Appendix F.4.1 multi-point stage and the native light-cache/dataset-item
CPU oracle can be checked against the independent full-hash reference vector without
allocating a DAG or starting mining:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --octopus-multipoint-self-test
```

`--octopus-cuda-self-test` uploads only the light cache and compares eight generated
256-byte DAG items with the CPU oracle; it does not allocate the full DAG. Full current
epoch allocation is an explicit diagnostic and is never run by `--probe` alone:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --octopus-cuda-self-test
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --probe --octopus-build-dag
```

The official public build's 0.75% developer fee and per-coin Binance Pool destinations
are compiled into the binary and visible in startup output and logs. The user may mine
to any compatible pool; fee windows use Binance Pool for the same selected coin and
algorithm. They never switch the GPU to RVN or another unrelated network. Unknown JSON
fields are rejected, so configuration cannot disable, reduce, or redirect the fee.
Every registered coin uses the embedded `KorayAltiner.Milena` developer worker at that
coin's own Binance Pool endpoint.
The user worker is always read explicitly from the selected configuration and printed at
startup. TrMadenci never falls back from a missing or rejected user identity to the
developer identity; all configured endpoints must reject authorization before startup
fails, and hashing does not begin without an authorized user connection.
Window length is chosen
cryptographically at random from 20–40 seconds while persistent accounting follows
the fixed long-run percentage. Short sessions are not charged before enough fee time
has accrued. As with every source-available application, this guarantee applies to the
official compiled binary; a person rebuilding modified source can change constants.

## Build

```powershell
dotnet build TrMadenci.sln
dotnet test TrMadenci.sln
```

Copy `trmadenci.example.json` to `trmadenci.json`, enter a pool and wallet/worker,
then run:

```powershell
dotnet run --project src/TrMadenci.Service
```

`coin` selects a validated network profile. `rvn` is enabled. `etc` and `cfx` are
registered as the next Binance-first GPU profiles but remain deliberately disabled until
their native engines and pool protocol paths pass qualification. TrMadenci never
redirects a fee window to a different coin or algorithm.

For a configuration-only smoke test, pass the sample path directly:

```powershell
dotnet run --project src/TrMadenci.Service -- trmadenci.example.json
```

Add `--probe` to verify the configured pool and worker without starting mining:

```powershell
dotnet run --project src/TrMadenci.Service -- trmadenci.json --probe
```

Start mining with:

```powershell
dotnet run --project src/TrMadenci.Service -- trmadenci.json --mine
```

An interactive terminal uses a three-region dashboard: fixed coin/network and session
analytics at the top, recent mining events in the middle, and live total power plus GPU
health on the bottom row. Redirected output automatically stays in line-oriented log
format. Pass `--plain-console` to force that format in an interactive terminal.

An empty `gpuDevices` array selects every detected CUDA GPU. The current CUDA build
targets NVIDIA compute capabilities 7.5, 8.6, 8.9 and 12.0 (RTX 20/30/40/50 families).
CUDA 13.1 is required to build the native component; published packages include the
NVRTC runtime files needed by the per-job program compiler. An NVIDIA display driver
with support for the packaged CUDA runtime is still required on the mining machine.

Use `--native-probe` together with `--probe` to verify CUDA discovery and calculate
the live job's KAWPOW epoch, light-cache size, and DAG size.

Add `--build-dag` for an explicit full-DAG build test. This allocates several GiB of
GPU memory and keeps individual CUDA launches short enough for Windows WDDM.
Use `--nonce-self-test` to build epoch 0 and verify the C# to CUDA search path against
official vector #0.

Use `--etchash-self-test` to verify the managed and native ECIP-1099 rules at the ETC
mainnet activation boundary. This test does not enable ETC mining or allocate a DAG:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.json --etchash-self-test
```

Add `--etchash-cuda-self-test` to upload the activation cache and compare sampled
256-parent CUDA DAG items byte-for-byte with the CPU reference. This uses a CUDA GPU,
but does not allocate the complete multi-gigabyte ETC DAG:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.json --etchash-self-test --etchash-cuda-self-test
```

Copy `trmadenci.etc.example.json` to an ignored local configuration such as
`trmadenci.etc.local.json`, replace the placeholder worker, and use that file with
`--probe` to verify an ETC worker without mining. Distributed example files intentionally
never default user rewards to the embedded developer identity.
Use `--etchash-build-dag` for the 2.5+ GiB activation DAG allocation test, or
`--etchash-nonce-self-test` for the official epoch-0 CUDA Hashimoto vector. ETC mining
remains gated even when these diagnostics pass; the session/share implementation is
present. Live accepted-share, reconnect and current-epoch performance qualification
pass; soak qualification remains before the public profile is enabled.

`--etchash-live-benchmark` connects and authorizes like `--probe`, builds the current
job's DAG, and measures a target-zero nonce batch without submitting any share:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --etchash-live-benchmark
```

For a controlled live ETC qualification run, set a real worker in the local configuration
and use the explicit gated mode below. It mines normally,
keeps the disclosed developer-fee policy active, and shuts down cleanly immediately
after the first pool-accepted share. It does not enable ETC for ordinary `--mine` use:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --mine --etchash-qualification
```

The duration-controlled soak mode records every ten-second status snapshot and mining
event under `artifacts/soak`, then writes a final JSON summary. The value is parsed with
an invariant decimal point and is limited to seven days:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --mine --etchash-soak-hours=24 --plain-console
```

Native build and reference-vector test:

```powershell
cd src/TrMadenci.Native
cmake --preset windows-x64-debug
cmake --build --preset windows-x64-debug
ctest --preset windows-x64-debug
```

Release qualification status and the remaining soak/signing gates are tracked in
[`docs/release-readiness.md`](docs/release-readiness.md).
