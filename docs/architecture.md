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
until hardware testing is requested; CFX mining therefore stays gated.

GPU health telemetry is read directly from the NVIDIA Management Library every status
interval. CUDA ordinals are mapped to NVML devices through PCI bus identifiers so
multi-GPU ordering cannot attach measurements to the wrong worker. Unsupported fields
are omitted without interrupting hashing. Telemetry is observational only: TrMadenci
does not change clocks, voltage, power limits or fan control.
