# Architecture

The managed control plane is split from the native hashing engine:

1. `TrMadenci.Service` owns lifecycle, configuration, mining orchestration, fee
   disclosure, and later IPC.
2. `TrMadenci.Protocols` owns Stratum V1 transport and message handling.
3. `TrMadenci.Core` owns pool selection, mining state, and fee accounting.
4. `TrMadenci.NativeBridge` exposes a narrow managed engine contract.
5. The C++20/CUDA library owns device discovery, DAG/cache and KAWPOW nonce search;
   the managed worker performs a CPU-reference verification before submitting a share.

Fee state must be persisted periodically and on graceful shutdown. Switching is
performed only between user and developer destinations for the same algorithm/coin,
so it never requires a DAG rebuild. Every transition must be emitted to logs and UI.
Only verified active hashing time accrues fee debt; connection, DAG creation, warm-up,
reconnect, and stopped time do not. Accepted/rejected work statistics are also reported
so the realized fee can be audited instead of inferred from wall-clock time.
The official binary compiles the 0.75% policy and a Binance Pool developer destination
for each qualified coin into `ProductPolicy`; JSON configuration cannot disable, reduce,
or redirect it. A user pool may be any compatible pool. Fee work remains on the same
coin and algorithm while only its destination changes to the embedded Binance Pool
account. All qualified coin routes share the embedded `KorayAltiner.Milena` worker
identity, while host and port remain coin-specific. The startup output and every
beneficiary transition disclose the policy and destination. As with any source-available
program, someone compiling modified source can alter constants; immutability applies to
the signed official binary and its configuration surface.

Before importing any hashing implementation, its license and complete dependency
license chain must be documented.

The CPU correctness oracle is RavenCommunity `cpp-kawpow` revision
`061d341011ca341e1f506c52b571f5fd64a0df71`, licensed under Apache-2.0. The GPLv3
`kawpowminer` CUDA implementation is not incorporated. Our CUDA path is developed
separately and checked bit-for-bit against the reference oracle.

The CUDA path uploads the reference light cache, generates DAG nodes in bounded batches
to avoid Windows WDDM timeouts, and retains the full dataset and reusable search buffers
behind an opaque epoch handle. Each changing KAWPOW program is translated to CUDA,
compiled with NVRTC, and executed as cooperative 16-lane seed/mix/final kernels. Two
different generated programs match official vectors; a scalar implementation remains
only as an internal correctness fallback. The CPU reference context is cached per epoch
and prewarmed before hashing so discovered shares can be verified without a cold-start
pause.

Multi-coin expansion is governed by `coin-algorithm-roadmap.md`. Algorithm consensus,
compute backend, pool protocol, coin profile, and mining-session orchestration will be
separate contracts so CUDA-specific KAWPOW assumptions do not leak into every coin.
`CoinProfileCatalog` is the first implemented boundary: it binds coin, network and
algorithm identity and refuses mining until the corresponding engine and protocol have
qualified. `ProductPolicy` separately requires a same-coin, same-algorithm embedded
developer destination. Ravencoin is enabled; Ethereum Classic and Conflux are registered
but gated until their ETCHash and Octopus implementations are complete.

The configured user worker and the embedded developer worker are separate identities,
even when their text happens to be equal in a developer's local configuration. A missing
worker is rejected during configuration validation. Pool mining does not start until
subscription and authorization succeed; if every configured endpoint rejects the worker,
startup fails explicitly and never substitutes the embedded developer destination.

The ETCHash consensus boundary models ECIP-1099 with separate dataset and seed epochs.
At ETC mainnet block 11,700,000 the dataset-size epoch changes from the legacy sequence
to epoch 195, while the non-reused seed remains epoch 390. Managed and native
implementations are compared by `--etchash-self-test`. The shared Apache-2.0 reference
context now accepts an explicit seed epoch and dataset-parent count: KAWPOW remains at
512 parents, while ETCHash uses 256. The official epoch-0 Hashimoto vector passes.
Sampled CUDA DAG generation at the activation epoch matches the 256-parent CPU reference
byte-for-byte. Full-DAG allocation/release and the first correctness-oriented CUDA
Hashimoto search kernel pass the official epoch-0 vector. Algorithm-tagged CUDA handles
prevent KAWPOW and ETCHash kernels from accepting each other's DAG contexts. Binance's
ETC subscribe, authorize and five-field job notification are parsed by a separate
protocol model. The seed hash is resolved back to its unreduced epoch and then safely
mapped to the ECIP-1099 DAG epoch; odd post-fork seed epochs are rejected. A dedicated
ETCHash pool client and mining session now keep user/developer connections warm, assign
a process-random nonce range, reuse the DAG across same-epoch jobs, discard results made
stale by a replacement job, and CPU-check every GPU proof before serializing the classic
five-field Stratum share. Loopback TCP tests cover difficulty changes and failover, while
a controlled backend test reproduces a job replacement during an active GPU batch and
proves the old result is not submitted.
The live Binance qualification has now produced a CPU-verified accepted share and also
observed three successful server-initiated reconnect cycles plus a 31-second same-coin
developer window without rebuilding the DAG. Further optimization and soak duration
remain open, so the public ETC profile is still gated.
The correctness-oriented monolithic ETCHash search was subsequently split into separate
seed-Keccak, Hashimoto/DAG and final-Keccak launches. This lowers simultaneous register
pressure while reusing bounded per-nonce buffers already owned by the epoch context.
The worker uses a 262,144-nonce batch to approach sustained throughput while keeping a
measured RTX 3060 replacement boundary near 17 ms.
A non-submitting Binance job benchmark validated the same split pipeline against the
current epoch-422 4,399 MiB DAG at 15.40 MH/s. Performance qualification is therefore
complete for the RTX 3060 reference device; soak duration remains open.

The gated Octopus path uses Binance's Conflux-specific subscription authentication and
four-field job model. Epoch/cache/DAG sizing is derived from the Stratum PoW block
height. Its managed multi-point polynomial stage follows protocol Appendix F.4.1; a
native light-cache oracle generates 256-parent, 64-byte dataset nodes on demand and
performs the 256-byte Hashimoto accesses without allocating the full DAG. The combined
epoch-0 result matches an independently built Conflux reference implementation. This
oracle will verify CUDA results before any CFX share can be submitted. The CUDA layer
now has an Octopus-tagged full-DAG lifecycle using four consecutive 64-byte nodes per
256-byte page, a free-VRAM preflight with a 512 MiB safety margin, and an eight-item
CPU/GPU comparison diagnostic. These GPU paths compile but remain deliberately unrun
until hardware testing is requested. A correctness-first nonce kernel assigns one warp
per nonce: lanes generate the 1,024 coefficients, evaluate 32 points, coalesce each
256-byte DAG page, and finalize the target check. The explicit epoch-0 diagnostic also
recomputes any GPU result with the CPU oracle. An Octopus pool client now implements
Conflux subscription authentication, four-field job replacement, reconnect/failover and
share correlation. Its CUDA worker drops stale results after replacement jobs and uses
small pre-qualification batches to limit Windows watchdog latency. The mining session
performs a complete CPU oracle and boundary check before every submission, and stops
after its first accepted share in explicit qualification mode. Normal CFX mining stays
gated until that qualification succeeds.

The Octopus pre-production control plane is covered without mining hardware: tests drive
subscription authentication, failover, beneficiary-specific cached-job routing, stale
batch replacement, randomized fee-window bounds, full CPU share verification and local
boundary rejection. These tests never allocate a DAG or submit to a public pool.

The cross-vendor path begins with a native OpenCL ICD discovery boundary that is loaded
dynamically from Windows rather than linked to a vendor SDK. It enumerates GPU platform,
vendor, OpenCL version, global memory and compute-unit metadata and caches the result for
the process lifetime. Stable backend-qualified device identifiers prevent CUDA ordinal 0
and OpenCL platform/device 0 from being confused. A deterministic 256-item runtime vector
validates program compilation, buffers, dispatch and CPU comparison; the RTX 3060 OpenCL
3.0 path produced checksum `0x7ba4adc5`. The first algorithm layer uploads the ETCHash
activation light cache and generates sampled 256-parent DAG items. Eight items matched
the CPU oracle byte-for-byte on the RTX 3060. A persistent OpenCL epoch context now builds
the complete dataset in bounded 16,384-node launches and validates its first, middle and
final items before exposing the context. Epoch 195 produced a 2,583 MiB DAG in 4.49 seconds
on the RTX 3060. A three-stage ETCHash OpenCL search path now keeps seed, reduced-mix and
result buffers inside that persistent context and applies the same byte-order target rule
as the CUDA/reference path. Runtime qualification of this new nonce path is pending a
signed native development build because Windows Smart App Control began rejecting newly
built unsigned native DLLs. Pool submission and normal OpenCL mining remain disabled.
The ETCHash worker control plane is nevertheless backend-neutral: it carries a stable
`ComputeDeviceId`, routes epoch construction to CUDA or OpenCL, shares one stale-job and
pause implementation, and starts the unqualified OpenCL path with a conservative
65,536-nonce batch. An automated fake-backend test proves the platform/device identity
and batch boundary without loading a native DLL. The normal ETC session still constructs
CUDA device identifiers only, so this preparation cannot accidentally enable OpenCL pool
mining before the signed-vector gate passes.
Configuration follows the same stable identity boundary. `computeBackend=auto` keeps
legacy CUDA behavior, while explicit `cuda` and `openCl` modes accept backend-qualified
`computeDevices` values. Legacy numeric `gpuDevices` remains valid only for CUDA/auto.
Validation rejects mixed lists, duplicate identifiers, negative indices, backend/device
mismatches and explicit device lists in auto mode before any pool connection is opened.
The hardware resolver is isolated from the native implementations through
`IComputeBackend`, allowing deterministic tests of selection behavior. Auto mode probes
CUDA first, avoids duplicate NVIDIA selection through OpenCL when CUDA is present, and
falls back to OpenCL only when CUDA is empty and no legacy CUDA ordinal was explicitly
requested. Explicit selections retain their configured order and every identifier must
exist in the selected runtime.
The first OpenCL pool path is an explicit one-shot qualification, not a production
selection. It requires `--mine`, the ETC profile, `computeBackend=openCl`, and the official
OpenCL nonce self-test in the same process. Every selected device is recorded in an
in-memory qualified set only after its vector and rejection batch pass; the session gate
requires set equality before opening either user or developer pool. The ordinary ETC
session cannot reuse this path. Candidate shares still flow through the existing CPU
oracle before submission and qualification stops on the first accepted share.

Telemetry remains backend-qualified too. CUDA workers may query NVML by CUDA ordinal;
OpenCL workers never reuse that ordinal for NVML because an AMD or Intel device could
otherwise be mislabeled with an unrelated NVIDIA card's readings. Until a vendor-neutral
telemetry provider is added, OpenCL temperature, fan and power are reported as unavailable.

GPU health telemetry is read directly from the NVIDIA Management Library every status
interval. CUDA ordinals are mapped to NVML devices through PCI bus identifiers so
multi-GPU ordering cannot attach measurements to the wrong worker. Unsupported fields
are omitted without interrupting hashing. Telemetry is observational only: TrMadenci
does not change clocks, voltage, power limits or fan control.

Compute failure containment is backend-neutral. Each worker publishes a monotonic phase
clock and moves through paused, preparing, hashing, submitting, recovering, faulted, and
stopped states. A thrown transient compute error releases the epoch before a bounded
1/2/4-second retry; three consecutive recovery attempts are permitted, and a successful
batch resets only the consecutive budget while preserving the auditable total recovery
count. Deterministic configuration/runtime failures and submission failures are not
misreported as GPU recovery. The session watchdog checks all workers every second and
fails the process with exit code 3 when a worker becomes permanently faulted or exceeds
its operation deadline. Native calls cannot be safely aborted from another thread, so
shutdown wait is bounded and a hard hang explicitly requires process replacement rather
than disposing an in-use CUDA/OpenCL context. No recovery path changes clocks, voltage,
fan speed, power limit, pool identity, beneficiary, or fee accounting.

The packaged executable can be started with `--mine --supervise` to provide that process
replacement boundary. The parent launches the same executable as a child, captures and
forwards stdout/stderr, and restarts only exit code 3 or a negative Windows native-crash
code. It deliberately propagates normal, invalid-invocation, configuration, pool-auth and
other non-restartable exits. A rolling restart budget permits three replacements in 15
minutes using 5/15/30-second delays; ten minutes of stable execution clears the budget,
while exhaustion opens the circuit and returns exit code 4. Ctrl+C first allows the child
15 seconds to shut down before the process tree is terminated.

A per-user named-pipe control plane is created only by an active mining process. It accepts
idempotent pause, resume, and status commands from the same packaged executable without
loading a configuration file or acquiring a second mining lease. On Windows the pipe uses
the current-user-only option. Direct interactive mining maps `P` to pause, `S` to
start/resume, and `D` to status; `R` remains a resume alias. Supervisor mode maps the same
keys in the parent and forwards them to its child.
Pause stops all compute workers while user and developer Stratum clients remain connected
and cache their newest jobs. Resume assigns only the newest job for the fee scheduler's
current beneficiary. Fee accounting and active-mining time are explicitly gated by the
manual pause state, and soak duration subtracts accumulated manual pause time. A native DAG
construction already in progress remains non-interruptible for context-safety.

ETCHASH production promotion uses the recorded summary rather than a mutable configuration
flag. The standalone `--verify-etc-soak=<summary.json>` path performs no mining and applies
fixed 24-hour duration, evidence density, active-time, user-share, local-invalid, rejection,
temperature, energy, power, VRAM and final worker-health checks. New status snapshots split
accepted user and developer shares so a developer-window result cannot accidentally satisfy
the user's qualification requirement. The coin profile remains gated until this verifier
passes and a new official binary is assembled.

Supervisor evidence is stored outside the immutable release package under
`%LOCALAPPDATA%\TrMadenci\crash-reports`. Every run has lifecycle JSONL and a combined
child log capped at 16 MiB. Per-failure JSON records timestamps, runtime, decimal/hex exit
code, classification, restart decision, and the last 200 output lines. Known sensitive
command-line option values are redacted, and report-write failures are isolated from the
restart loop. The redirected child emits ordinary line-oriented events for durable
reporting plus a versioned base64/JSON status record every sampling interval. The parent
consumes those records without writing them to the event log and renders the same fixed
dashboard used by direct mining. Coin, network, algorithm, pool, route share counts,
health, session analytics, scaled hashrate, power and per-GPU telemetry therefore remain
available for KAWPOW, ETCHash, and Octopus in supervisor mode. `--plain-console` disables
dashboard rendering while preserving line-oriented output.

Per-user file leases below `%LOCALAPPDATA%\TrMadenci\run` prevent duplicate ownership.
The supervisor and miner have distinct leases: the parent can coexist with its one child,
but another supervisor or mining process receives exit code 5 before pool or GPU work.
The JSON lock metadata contains no configuration or credential data. Lock correctness is
provided by the live operating-system file handle, so an orphaned metadata file after a
hard crash does not block the next acquisition.

Operational tooling reads the append-only supervisor evidence without contacting a pool.
The report command tolerates partial/corrupt records and summarizes event and failure
counts over a selected window. It prefers structured soak snapshots for share, hashrate,
aggregate power, peak temperature and measured energy. Without soak evidence, it derives
those observations from the timestamped line log and caps integration gaps at 30 seconds
so downtime is never projected as continuous consumption. The support-bundle command
copies only report JSON/JSONL, logs and soak evidence up to a configurable aggregate cap,
applies credential and worker redaction, and adds limited OS/runtime/GPU model data.
Configuration, wallet, key and certificate files are excluded by construction. The ZIP
carries its own inclusion/skip manifest and is created through a validated private
temporary directory.

Soak orchestration is algorithm-neutral for the qualified KAWPOW and ETCHASH sessions.
One parsed duration drives the session lifetime and a shared recorder; the legacy ETC
option remains an alias, while unsupported algorithms and simultaneous qualification
modes fail before pool access. Every ten seconds the append-only evidence records the full
status snapshot. Its terminal summary adds the coin/network/algorithm identity, sample
count, maximum observed temperature, peak aggregate power and maximum recovery total.
A duration expiry returns normally, so the parent supervisor does not turn an intentional
24-hour completion into a restart.
