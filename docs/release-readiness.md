# KAWPOW release readiness

Snapshot: 2026-09-12

## Completed

- All 13 official CPU KAWPOW vectors pass.
- CUDA DAG samples match the CPU reference byte-for-byte.
- CUDA JIT search matches official vectors from two different generated programs.
- Live Binance Stratum subscribe, authorization, job updates and share submission pass.
- A live RTX 3060 run reached 11.8–12.4 MH/s and submitted an accepted share with no
  rejected or locally invalid share in the validation run.
- Every GPU result is recomputed by the CPU reference before network submission.
- User and developer connections are kept warm; disconnect pauses the selected work,
  reconnect waits for a fresh job, and the user destination cycles to its failover.
- The 0.75% fee, 20–40 second random window bounds, and same-coin Binance Pool
  developer destinations are compiled into `ProductPolicy`; configuration attempts to
  override them are rejected. A user may independently select any compatible pool.
- Fee debt survives graceful restarts and advances only while a worker is hashing.
- Debug and Release native tests and all managed tests pass without warnings.
- A self-contained Windows x64 RC package was built with its NVRTC dependencies,
  notices and example configuration; the packaged executable passed the CUDA nonce
  self-test on an RTX 3060.
- NVML telemetry reports per-GPU temperature, fan, power, utilization, VRAM, clocks
  and MH/s/W without changing device settings; the fields were validated under load.
- The interactive console keeps coin/network and session analytics fixed at the top,
  recent mining events in the middle, and active power/GPU health fixed at the bottom;
  redirected output remains compatible with line-oriented log collectors.
- Every CUDA/OpenCL worker now releases and rebuilds its epoch after transient compute
  failure with at most three retries and 1/2/4-second backoff. Successful search resets
  the consecutive-failure budget; deterministic configuration/runtime errors fail
  immediately. A monotonic watchdog covers search, DAG preparation and share submission,
  shutdown is bounded if native code cannot return, and exit code 3 identifies a required
  process restart. Worker phase, recovery count and last error flow into dashboard and
  soak evidence. Controlled tests cover recovery, retry exhaustion, watchdog timeout and
  a KAWPOW job replacement crossing an epoch boundary without submitting stale work.
- The packaged apphost now has an opt-in parent supervisor. It restarts only watchdog
  exits and native crashes, applies 5/15/30-second backoff, opens a circuit after three
  restarts in 15 minutes, and resets its budget after a stable ten-minute run. Normal,
  invocation, configuration and authentication exits are never looped. Each failure gets
  a redacted JSON report plus bounded recent output; lifecycle JSONL and the combined log
  are kept outside the package under the user's local application-data directory.
  Unit and Windows child-process integration tests cover classification, backoff, budget
  reset, redaction, output retention, restart and circuit-open behavior.
- Separate per-user supervisor and miner file leases reject duplicate processes before
  pool/GPU work while still allowing the supervised parent-child pair. Read-only report
  summarization includes failures, share events, hashrate, power, temperature and bounded-
  gap estimated energy. A sanitized support-bundle generator is included in publish output.
  The bundle excludes configuration/private-key material, redacts known secrets and worker
  identity (including startup developer-destination and pool-ready forms), refuses
  overwrite, and records included/skipped files. A replacement bundle from the live RVN
  soak passed a zero-known-identifier scan. No automatic startup mechanism is installed
  or shipped.
- The former ETC-only soak runner is now shared by KAWPOW and ETCHASH through
  `--soak-hours`. It retains the ETC command alias, rejects qualification conflicts and
  unsupported algorithms before pool access, stops cleanly at the requested duration,
  and records peak temperature, aggregate power and recovery totals in its final summary.
  Sanitized support bundles can include both supervisor and soak evidence.
- Manual pause/resume is shared by KAWPOW, ETCHASH and Octopus. Direct and supervisor
  terminals accept `P`=pause, `S`=start/resume and `D`=status; `R` remains a resume alias,
  and a second invocation can use
  `--pause`, `--resume`, or `--mining-status` without a configuration file. Pools stay
  connected and cache fresh work while workers are paused. Fee, active-mining and soak
  duration counters do not advance; pause count/duration are retained in soak summaries.
- A six-minute RC9 RVN/KAWPOW Supervisor soak completed normally on the RTX 3060 and
  exercised the published duration path. The DAG built in 35.96 seconds, active mining
  averaged 12.74 MH/s, eight user shares were accepted with zero rejected or locally
  invalid shares, measured energy was 0.016154 kWh, maximum temperature was 75 C, and
  peak aggregate power was 170.3 W. There were no worker recoveries, supervisor restarts,
  or crash reports; exit code 0 was correctly treated as final. The product owner accepted
  this as the successful single-GPU RVN qualification on 2026-09-12. The recorded duration
  remains six minutes and is not misrepresented as 24-hour/multi-GPU evidence; those and
  second-pool coverage remain broader public-release gates.
- The unsigned `0.1.0-rc14` package is assembled from the current Release native build,
  contains all required runtime and example files, excludes local worker configuration,
  and is ready for publisher signing. Its packaged native DLL matches the Release build
  at SHA-256 `51EA656070ACCDCED52ACFB5FF0D950BB95CB3642C295EB1E5B563C27D1DEFD4`;
  the unsigned service apphost is
  `AA839AB4512EA5236FF85220679A38169BB4C469BE254A85D473870C2E8D2989`.
- The Release solution build is warning-free and all 141 hardware-independent tests pass,
  including pause timing/idempotence, same-user named-pipe control, and paused Octopus
  fresh-job routing. Supervisor status-transport integration and the `P`/`S`/`D`/`R`
  keyboard map are also covered. No mining was started while validating RC14.

## Release gates still requiring operational time or credentials

- Run a 24-hour multi-GPU soak and retain accepted/rejected, temperature and memory
  observations. A short successful run is not evidence for this duration-dependent gate.
- Qualify against a second independent Ravencoin pool or a local reference node,
  including deliberate disconnect, failover, stale-job and difficulty-change scenarios.
- Malware-scan the RC package and sign its binaries with the publisher's code-signing
  certificate before public distribution. A certificate is intentionally not generated
  or impersonated by the repository. `scripts/Sign-TrMadenci.ps1` accepts only a real
  code-signing certificate already present in the Windows certificate store and signs
  every first-party application EXE, managed DLL and native DLL without handling PFX
  passwords. The independent `scripts/Test-TrMadenciSignatures.ps1` gate requires one
  matching publisher, trusted timestamps, and valid Authenticode on every third-party
  EXE/DLL in the package. The release-manifest scripts then record and verify SHA-256,
  length, missing-file and unexpected-file integrity for the complete assembled package.
  `scripts/Invoke-TrMadenciOpenClReleaseGate.ps1` combines those read-only checks with
  the explicit same-process ETCHash OpenCL qualification after mining is acknowledged.
- Record a real device-loss/recovery observation and a multi-GPU test on each supported
  compute-capability family intended for the public release.

Until those external qualification gates pass, describe this build as a production
candidate rather than a final production release.

## ETC expansion status

- ECIP-1099 activation block, 30,000/60,000 block epoch rules, dataset sizing and the
  distinct non-reused seed epoch are implemented in managed and native code.
- The activation boundary self-test passes at block 11,700,000 with dataset epoch 195,
  seed epoch 390, a 40 MiB cache and a 2,583 MiB DAG.
- Eight CUDA-generated DAG items at the activation boundary match the 256-parent CPU
  reference byte-for-byte without changing the KAWPOW 512-parent result.
- The RTX 3060 built and released the 2,583 MiB activation DAG in 7.80 seconds. The first
  CUDA Hashimoto kernel matches CPU mix/final output and the official epoch-0 nonce
  vector. A stable 1,048,576-nonce epoch-0 benchmark selected a 256-thread launch at
  10.37-10.43 MH/s on the RTX 3060 (versus 10.05-10.08 at 128 threads and 9.49-9.56
  at 512); this remains a correctness-oriented kernel rather than a final performance
  claim.
- Binance ETC subscribe/authorize and live five-field job parsing pass with the embedded
  developer worker. A live seed resolved to seed epoch 844 / DAG epoch 422.
- The dedicated ETC session now implements randomized nonce allocation, same-coin
  user/developer destination switching, stale-result suppression, reconnect/failover,
  classic five-field share serialization, and CPU verification before submission.
- A loopback TCP integration test covers subscribe, authorize, epoch resolution and the
  complete accepted-share payload, including a `mining.set_target` difficulty update.
  A second test deliberately drops the primary socket and verifies failover waits for
  fresh work before resuming. A controlled GPU-backend race test proves that a solution
  from a replaced batch is discarded and only the new job can be submitted.
- A controlled Binance ETC qualification ran for 33 minutes 34 seconds on the RTX 3060.
  The current epoch-422 DAG built in 13.75 seconds; live hashing stabilized around
  9.84-10.40 MH/s at 68-69 C and roughly 124-127 W with about 5.65 GiB VRAM in use.
  Three server-initiated disconnects were observed at ten-minute intervals: hashing
  paused, both sockets reauthorized after five seconds, and work resumed only after a
  fresh job without rebuilding the DAG. A 31-second disclosed developer window switched
  destinations without a DAG rebuild. The first CPU-verified share was accepted by
  Binance with nonce `0xc5a6b9efe551d2545`; final counts were accepted=1, rejected=0,
  local-invalid=0, and qualification mode shut down automatically.
- After live qualification, the monolithic ETCHash kernel was split into seed Keccak,
  Hashimoto/DAG and final Keccak stages to reduce register pressure. The official vector
  remains bit-exact. On the RTX 3060 epoch-0 benchmark, the sustained 1,048,576-nonce
  rate improved from roughly 10.4 MH/s to 15.26-15.59 MH/s. A 32-thread Hashimoto block
  was fastest, and the worker now uses a 262,144-nonce batch measured at about 16.9-17.5
  ms, preserving responsive stale-job replacement. Current-epoch live performance must
  be remeasured during soak; epoch-0 figures are not presented as pool earnings rates.
- A non-submitting live benchmark then resolved Binance seed epoch 844 / DAG epoch 422,
  built the current 4,399 MiB DAG in 12.95 seconds and measured 15.40 MH/s over a
  1,048,576-nonce target-zero batch. No share was submitted by this benchmark. This is
  about 48% above the pre-split live qualification band of 9.84-10.40 MH/s.
- ETC remains disabled until the soak gate passes.

RC14 retains the standalone, read-only `--verify-etc-soak=<summary.json>` promotion gate. It
requires a completed ETC/ETCHASH soak requested for at least 24 hours, at least 95% active
mining time, adequate status density, one accepted user-beneficiary share, zero locally
invalid shares, no more than 5% rejected shares, positive power/energy/VRAM evidence, peak
temperature below 85 C, and no final `Faulted`/`Recovering` worker. Status snapshots now
separate user and developer accepted shares, and summaries retain maximum VRAM per device.
The ETC profile is not enabled merely because this code exists; it will be promoted only
after real evidence passes the fixed verifier.

The duration-controlled soak runner writes ten-second JSONL status/event evidence and a
final JSON summary under `artifacts/soak`, including process id, wall-clock/active time,
shares, invalid results, energy and per-GPU health. It now uses the shared KAWPOW/ETCHASH
recorder; the previously captured ETC evidence remains valid. A 36-second live smoke-soak
completed
automatically with valid JSONL, 14.60 MH/s at its final sample, zero invalid shares and a
clean summary. This validates the runner, not the required 24-hour duration.

A planned 24-hour RTX 3060 run started on 2026-09-11 at 16:10:36 +03:00 but was stopped
at the user's request shortly after startup because the machine was unattended. Its
partial append-only evidence remains at
`artifacts/soak/etchash-soak-20260911-161036.jsonl`; it does not satisfy the soak gate.

A second ETC run on 2026-09-12/13 produced 2:08:35 wall-clock and 1:59:27 active evidence,
including one 8:18 manual pause. It averaged 15.16 MH/s, accepted 14 user shares with zero
rejected and zero locally invalid shares, reached 69 C and 131.703 W maximums, recorded
0.260914 kWh, used at most 6.28 GiB VRAM, and had zero worker recoveries. The operator
stopped it with Ctrl+C while paused, so its outcome is correctly `cancelled`; this is strong
partial stability evidence but does not satisfy the fixed 24-hour qualification gate.

## OpenCL expansion status

- Dynamic OpenCL discovery and a deterministic compile/buffer/CPU comparison test pass
  on the RTX 3060 without relying on CUDA device indexes.
- The ETCHash activation DAG was built persistently through OpenCL in 4.49 seconds using
  2,583 MiB; its first, middle and final items match the CPU oracle byte-for-byte.
- The split OpenCL seed, Hashimoto and final-target nonce path is wired to the
  backend-neutral ETC worker. The official epoch-0 vector, target-zero rejection and
  first accepted pool share form one same-process qualification gate.
- Windows Smart App Control activated before that native nonce diagnostic could execute
  and rejects the newly built unsigned `TrMadenci.Native.dll`. No pool connection or
  share submission was started after the block. Qualification remains pending until the
  `rc14` binaries receive a trusted publisher signature; Smart App Control is not disabled
  and a self-signed root is not installed as a workaround.
