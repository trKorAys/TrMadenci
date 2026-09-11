# KAWPOW release readiness

Snapshot: 2026-09-11

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

## Release gates still requiring operational time or credentials

- Run a 24-hour multi-GPU soak and retain accepted/rejected, temperature and memory
  observations. A short successful run is not evidence for this duration-dependent gate.
- Qualify against a second independent Ravencoin pool or a local reference node,
  including deliberate disconnect, failover, stale-job and difficulty-change scenarios.
- Malware-scan the RC package and sign the executable with the publisher's code-signing
  certificate before public distribution. A certificate is intentionally not generated
  or impersonated by the repository.
- Add a watchdog/recovery policy for CUDA device loss and record a multi-GPU test on
  each supported compute-capability family intended for the public release.

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

The duration-controlled soak runner writes ten-second JSONL status/event evidence and a
final JSON summary under `artifacts/soak`, including process id, wall-clock/active time,
shares, invalid results, energy and per-GPU health. A 36-second live smoke-soak completed
automatically with valid JSONL, 14.60 MH/s at its final sample, zero invalid shares and a
clean summary. This validates the runner, not the required 24-hour duration.

A planned 24-hour RTX 3060 run started on 2026-09-11 at 16:10:36 +03:00 but was stopped
at the user's request shortly after startup because the machine was unattended. Its
partial append-only evidence remains at
`artifacts/soak/etchash-soak-20260911-161036.jsonl`; it does not satisfy the soak gate.
