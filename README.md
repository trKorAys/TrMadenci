# TrMadenci

TrMadenci is a transparent multi-algorithm GPU miner for Windows. The current production
candidate supports Ravencoin/KAWPOW, with ETCHash and Octopus kept behind explicit
qualification gates, Stratum V1 failover, multiple GPUs, and CUDA/OpenCL backends.

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
- Gated Octopus session with hardware-free authentication/failover, fee-routing,
  stale-work and CPU share-verification coverage
- Dynamic OpenCL GPU discovery plus CPU-oracle-verified ETCHash sample DAG generation
  for the future AMD/Intel backend

The Binance-first multi-coin expansion order and its support criteria are documented in
[`docs/coin-algorithm-roadmap.md`](docs/coin-algorithm-roadmap.md). New algorithms do
not begin until the production KAWPOW search/share pipeline meets those criteria.

The cross-vendor backend now has a dynamically loaded OpenCL discovery layer. The probe
only reports installed GPU platforms and devices; it does not compile a kernel or mine:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.json --opencl-probe
```

The explicit runtime test compiles a deterministic OpenCL program, transfers input and
output buffers and verifies all 256 results on the CPU. It does not mine or connect to a
pool:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.json --opencl-self-test
```

The ETCHash OpenCL validation uploads only the activation light cache, generates eight
256-parent DAG items, and compares every byte with the CPU oracle. It does not allocate
the complete DAG, search nonces, connect to a pool, or mine:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --etchash-opencl-self-test
```

Full OpenCL epoch allocation remains an explicit diagnostic. It builds the activation
DAG in bounded batches, verifies the first, middle and final items against the CPU oracle,
then releases the persistent context. It does not search nonces or connect to a pool:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --etchash-opencl-build-dag
```

The first OpenCL nonce path uses separate seed, DAG-mix and final-target kernels with
reusable per-epoch search buffers. Its explicit epoch-0 diagnostic checks the official
nonce and hash against the CPU oracle, then runs a target-zero rejection batch:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json --etchash-opencl-nonce-self-test
```

This path remains diagnostic-only and is not selected by normal ETC mining.

After the native binaries are signed, OpenCL pool qualification is deliberately tied to
the nonce test in the same process. Configure `computeBackend` as `openCl`, optionally
select `computeDevices`, then run:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.etc.local.json `
  --mine --etchash-opencl-nonce-self-test --etchash-opencl-qualification
```

Every selected device must pass the official epoch-0 vector and target-zero rejection
batch before either pool connection can begin. Qualification keeps the same-coin 0.75%
fee policy active, CPU-verifies every candidate, and stops after the first pool-accepted
share. It does not enable ordinary OpenCL mining or an OpenCL soak run.

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

The correctness-first nonce kernel has a separate explicit epoch-0 test. It allocates
approximately 4 GiB, searches exactly one known nonce, and then CPU-verifies the result:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --octopus-nonce-self-test
```

The Conflux subscription/reconnect client, stale-work-safe CUDA worker and mining session
are present. Normal CFX `--mine` remains blocked. After the explicit GPU vector succeeds,
the pre-production session can be invoked with the qualification gate below; it locally
recomputes every candidate with the CPU oracle and stops after the first accepted share:

```powershell
dotnet run --project src/TrMadenci.Service -c Release -- trmadenci.cfx.local.json --mine --octopus-qualification
```

Do not run qualification before `--octopus-nonce-self-test` passes on the target GPU.

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

While the mining terminal is focused, press `P` to pause hashing, `S` to start/resume from
the freshest job for the currently selected beneficiary, or `D` to print the current
control state. `R` remains a resume alias for compatibility. `Ctrl+C` remains a full
graceful shutdown. Manual pause keeps both pool connections
warm and continues caching job updates, but does not advance active-mining, developer-fee,
or duration-controlled soak time. The GPU finishes only its already-running bounded search
batch before becoming idle; an in-progress native DAG build cannot be interrupted safely.

The same controls work without focusing the mining window. Run exactly one standalone
command from a second terminal; no configuration path is required:

```powershell
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe --pause
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe --mining-status
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe --resume
```

Every CUDA/OpenCL worker has a bounded recovery policy. A transient compute failure
releases the current epoch and retries at most three times after 1, 2, and 4 seconds;
one successful search batch resets the consecutive-failure budget. The dashboard and
soak JSONL expose preparing, hashing, submitting, recovering, faulted, and stopped
states plus the cumulative recovery count. A search with no progress for 30 seconds,
DAG preparation exceeding five minutes, or share submission exceeding 45 seconds trips
the watchdog. Shutdown waits at most five seconds for a blocked native call and the
process exits with code 3 so a service manager can restart it. TrMadenci never resets,
overclocks, or otherwise changes GPU settings as part of recovery.

The packaged apphost can supervise its own mining child process. Supervisor mode restarts
only watchdog exits (code 3) and native Windows crashes; normal shutdown, invalid command
lines, and configuration/authentication failures are not placed in restart loops. It
allows three restarts in a rolling 15-minute window with 5/15/30-second backoff, resets
that budget after a stable 10-minute run, and exits with code 4 if the circuit opens:

```powershell
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe `
  .\trmadenci.json --mine --supervise
```

Crash JSON, lifecycle JSONL, and a capped combined child log are written below
`%LOCALAPPDATA%\TrMadenci\crash-reports`. Reports retain the most recent 200 child-output
lines and redact sensitive command-line values. The supervised child emits a private,
versioned status stream to its parent, so the same fixed dashboard is shown for KAWPOW,
ETCHash, and Octopus while ordinary child events remain in the report log. Hashrate units
scale automatically from H/s through GH/s. Pass `--plain-console` when line-oriented
terminal output is preferred. The supervisor terminal handles `P`=pause, `S`=start/resume,
`D`=status, and the legacy `R`=resume alias through the local control channel. Ctrl+C
requests a graceful stop and force-terminates the child tree only if it has not exited
after 15 seconds.

Mining and supervisor processes use separate per-user file leases under
`%LOCALAPPDATA%\TrMadenci\run`. This lets one supervisor own one mining child while a
second direct miner or supervisor is rejected with exit code 5. The lock records only
process id, role, start time, and executable path; a stale file after a hard crash is safe
because the operating-system file lease, not the file's presence, determines ownership.

Summarize recent supervisor activity without changing system state. The report prefers
structured soak counters and otherwise uses captured mining logs to calculate accepted/
rejected/local-invalid events, latest/average/maximum hashrate, latest/average power,
peak temperature, and energy for the selected window:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Get-TrMadenciSupervisorReport.ps1 -Hours 24
```

Create a bounded, sanitized support ZIP containing supervisor reports, recent soak
evidence, and non-identifying runtime/GPU details:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\New-TrMadenciSupportBundle.ps1 -Hours 24
```

The bundle never includes TrMadenci configuration files or private keys. Known secret
arguments, credential-bearing URLs, JSON secret fields, and the displayed user worker are
redacted. It refuses to overwrite an existing ZIP and includes a manifest of collected or
skipped files. Published packages place both commands in their `tools` directory. Neither
tool starts mining, installs a service, or creates an automatic-start task.

An empty `gpuDevices` array selects every detected CUDA GPU. The current CUDA build
targets NVIDIA compute capabilities 7.5, 8.6, 8.9 and 12.0 (RTX 20/30/40/50 families).
CUDA 13.1 is required to build the native component; published packages include the
NVRTC runtime files needed by the per-job program compiler. An NVIDIA display driver
with support for the packaged CUDA runtime is still required on the mining machine.

`computeBackend` accepts `auto`, `cuda`, or `openCl`. `auto` preserves the current
production behavior and prefers CUDA. Existing numeric `gpuDevices` selections remain
valid for CUDA. New backend-qualified selections use `computeDevices`, for example:

```json
{
  "computeBackend": "cuda",
  "computeDevices": ["cuda:0"]
}
```

The future cross-vendor form is `"computeBackend": "openCl"` with identifiers such as
`"opencl:0:0"` (platform 0, device 0). `gpuDevices` and `computeDevices` cannot be used
together, and explicit devices cannot be combined with `auto`. OpenCL mining remains
gated even when selected; the option is present now so configuration validation and the
worker boundary can stabilize before signed GPU qualification.

The tested automatic resolver enumerates CUDA first and does not initialize OpenCL when
CUDA devices are available. If CUDA is absent and no legacy CUDA indexes were requested,
it falls back to OpenCL. A legacy `gpuDevices` list is never reinterpreted as OpenCL
platform/device indexes. The fallback is prepared but remains blocked from pool mining
by the OpenCL qualification gate.

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

The duration-controlled `--soak-hours` mode supports KAWPOW and ETCHASH, records every
ten-second status snapshot and mining event under `artifacts/soak`, then writes a final
JSON health summary with share totals, energy, peak temperature, peak aggregate power,
and maximum recovery count. The value uses an invariant decimal point and is limited to
seven days. Manual pause time is excluded from the requested duration and is recorded
separately in the final summary; wall-clock duration therefore grows while paused.
Supervisor treats the clean duration exit as final and does not restart it:

```powershell
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe `
  .\trmadenci.json --mine --supervise --soak-hours=24
```

Use the same option with `trmadenci.etc.local.json` for the pending ETC soak. The previous
`--etchash-soak-hours` spelling remains compatible for ETC configurations. Soak cannot be
combined with one-shot qualification modes, and CFX remains excluded until its GPU and
accepted-share qualification gates pass.

After an ETC soak completes, validate its recorded summary without loading a mining
configuration or connecting to a pool:

```powershell
.\artifacts\TrMadenci-0.1.0-rc14-win-x64\TrMadenci.Service.exe `
  --verify-etc-soak=.\artifacts\soak\etc-soak-YYYYMMDD-HHMMSS.summary.json
```

The fixed production gate requires a completed ETC/ETCHASH run requested for at least
24 hours, at least 95% active mining time, adequate ten-second evidence coverage, one
user-beneficiary accepted share, zero locally invalid shares, at most 5% rejected shares,
positive power/energy/VRAM observations, a peak below the 85 C critical threshold, and no
GPU ending in `Faulted` or `Recovering`. Manual pause remains allowed and extends wall time.

Native build and reference-vector test:

```powershell
cd src/TrMadenci.Native
cmake --preset windows-x64-debug
cmake --build --preset windows-x64-debug
ctest --preset windows-x64-debug
```

Release qualification status and the remaining soak/signing gates are tracked in
[`docs/release-readiness.md`](docs/release-readiness.md).

## Release signing

Release signing uses a real code-signing certificate already installed in the Windows
`CurrentUser/My` or `LocalMachine/My` certificate store. Private keys and PFX passwords
must never be committed to this repository. Sign an assembled package by supplying the
certificate's 40-character thumbprint:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Sign-TrMadenci.ps1 `
  -PackagePath .\artifacts\TrMadenci-0.1.0-rc14-win-x64 `
  -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

The signing command signs every first-party `TrMadenci.*.exe` and `TrMadenci.*.dll`,
including the managed assemblies and native bridge, with SHA-256 and a trusted timestamp.
It verifies each result immediately. Verify an existing package without modifying it:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-TrMadenciSignatures.ps1 `
  -PackagePath .\artifacts\TrMadenci-0.1.0-rc14-win-x64 `
  -ExpectedThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

The verifier requires every first-party binary to use the same expected publisher and
also checks that every third-party EXE/DLL in the package still has a valid Authenticode
signature. After signing and before creating an archive, record and verify the complete
package inventory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-TrMadenciReleaseManifest.ps1 `
  -PackagePath .\artifacts\TrMadenci-0.1.0-rc14-win-x64

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-TrMadenciReleaseManifest.ps1 `
  -PackagePath .\artifacts\TrMadenci-0.1.0-rc14-win-x64
```

The manifest gate rejects missing, changed, and unexpected files. Authenticode remains
the source of publisher trust; the manifest is the reproducible inventory for the exact
assembled package and must be regenerated after any intentional package change.

Once the signed package, manifest, and an ETC configuration with `computeBackend` set to
`openCl` are ready, one explicit command checks both release gates and then runs the
same-process OpenCL nonce and pool qualification. It mines with the disclosed fee policy,
submits shares, and stops after the first accepted share:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-TrMadenciOpenClReleaseGate.ps1 `
  -PackagePath .\artifacts\TrMadenci-0.1.0-rc14-win-x64 `
  -ConfigurationPath .\trmadenci.etc.local.json `
  -ExpectedThumbprint YOUR_CERTIFICATE_THUMBPRINT `
  -AcknowledgeMining
```

`-ExecutionPolicy Bypass` applies only to that child PowerShell process; it does not
change the machine or user execution-policy setting. Review the repository-local script
before invoking it. The signing script deliberately refuses missing, expired, mismatched,
or private-key-less certificates, while the verifier also requires a trusted timestamp.
