# Coin and algorithm roadmap

Research snapshot: 2026-09-19

## Product boundary

TrMadenci must distinguish two capabilities:

1. A native GPU hashing engine for consumer graphics cards.
2. A native CPU hashing engine for CPU-oriented algorithms.

ASIC configuration, monitoring and proxy functions are explicitly outside the product
scope. Dedicated ASICs retain their own firmware and management software; TrMadenci does
not imitate ASIC algorithms on consumer GPUs.

Adding a coin name is not enough. A production-ready profile needs a consensus-correct
algorithm, the pool's Stratum dialect, job serialization, target conversion, share
submission, wallet validation, failover behavior, test vectors, and hardware tuning.

## Recommended order

| Priority | Algorithm / capability | Initial networks | Hardware | Decision |
| --- | --- | --- | --- | --- |
| P0 | KAWPOW | Ravencoin | NVIDIA GPU | End-to-end single-GPU qualification accepted by the product owner; retain second-pool, multi-GPU and signed-package coverage as broader public-release gates. |
| P1 | ETCHash | Ethereum Classic | GPU / specialized hardware | In progress: full CUDA DAG, official CPU/GPU vector, Binance job/epoch handling, session/reconnect, accepted live share and current-epoch split-kernel performance complete; next complete soak qualification. |
| P2 | Octopus | Conflux | High-VRAM NVIDIA GPU | Epoch/cache/DAG sizing, Binance client, full CPU oracle, CUDA DAG/worker, CPU-verified qualification session, pause-aware soak recorder and fixed production verifier implemented. Keep gated pending GPU vector execution, optimization, accepted live-share qualification and 24-hour soak. |
| P2 | Cross-vendor GPU backend | Existing algorithms | AMD and Intel GPU | OpenCL ICD discovery, stable device IDs, strict configuration and tested CUDA-first/OpenCL-fallback resolution are complete, together with light-cache upload and a CPU-verified full ETCHash epoch/DAG lifecycle. The split nonce-search kernel, backend-neutral ETC worker and same-process vector-to-pool qualification gate are implemented but remain disabled until executed on a signed native build; CUDA stays the optimized NVIDIA backend. |
| P3 | RandomX v1 | Monero | CPU | In progress: gated Monero profile, CPU-only configuration, vendored BSD-3-Clause v1.2.3 reference, official light-mode vector pass, persistent full-memory dataset/per-worker VM lifecycle, JSON-RPC job/login/submit codecs and a loopback-tested fail-closed pool client. Next implement the nonce worker/session, seed rollover, CPU telemetry, XMR developer wallet, accepted share and soak. GPU execution is technically possible but intentionally not offered because RandomX is CPU-optimized and GPUs are disadvantaged. |
| Deferred | Equihash | Zcash | ASIC-dominated | Binance Pool and Spot are available, but a consumer-GPU implementation is not a competitive priority. |
| Deferred | ETHash | EthereumPoW | GPU / specialized hardware | Binance Pool support alone is insufficient while there is no active Binance Spot market. |
| Out of scope | SHA-256, Scrypt, X11, Eaglesong, kHeavyHash and other ASIC workloads | BTC/BCH, LTC/DOGE, DASH, CKB, Kaspa and similar networks | ASIC | Use the ASIC vendor's firmware and management software; do not add noncompetitive GPU kernels or an ASIC controller to TrMadenci. |

As of the 2026-09-12 review, no remaining Binance Pool coin satisfies all three of the
initial expansion constraints: active Binance Spot/deposit support, economically useful
consumer-GPU mining, and a new algorithm. ETHW is GPU-mineable with Ethash and remains in
Binance Pool, but Binance explicitly states that it is not listed and deposits are not
supported. ZEC is pooled and traded, but current Zcash guidance says network difficulty
requires ASIC hardware in practice. The next implementation milestone is therefore the
OpenCL compute-backend boundary for AMD and Intel GPUs; ETHW and Equihash remain gated
until the product requirements change.

The first expansion phase requires both a qualified Binance Pool route and an active
Binance Spot market. Market availability changes over time and must be checked at release
time rather than treated as a permanent consensus property. Expected earnings also
change with price, network difficulty, reward, pool fee and electricity tariff. A later
profitability service must timestamp all inputs and show TRY/kWh assumptions instead of
labeling one coin as permanently "best".

## Why these choices

- Ravencoin documents KAWPOW as its current consumer-GPU-oriented algorithm.
- Ethereum Classic uses ETCHash and retains GPU support, although specialized hardware
  competition must be communicated honestly.
- Conflux uses Octopus and documents NVIDIA GPU mining with sufficient VRAM.
- Octopus uses 524,288-block epochs, starts with an approximately 4 GiB DAG and grows
  by approximately 16 MiB per epoch. TrMadenci calculates the exact prime-sized
  cache/DAG and reserves extra device memory before declaring a GPU eligible. Memory
  sizing must use the PoW block height supplied by the Stratum job, not Conflux RPC's
  differently defined epoch or block counters. A Binance Pool job observed on
  2026-09-11 reported height 156,498,021 (Octopus epoch 298), whose DAG is approximately
  8.656 GiB and fits a 12 GiB RTX 3060 with the current safety reserve.
- Monero's RandomX reference is BSD-3-Clause, exposes a C API, and is optimized for
  ordinary CPUs. Fast mining mode shares an approximately 2,080 MiB dataset, while
  light verification mode uses about 256 MiB and is substantially slower. It belongs in
  a separate CPU worker with explicit thread, huge-page and secure-JIT controls. GPU
  implementations exist, but the upstream project explicitly documents their
  disadvantage; adding one would increase maintenance without being the preferred path.
- Kaspa's own mining documentation says mainnet mining is now ASIC-only in practical
  terms. Alephium likewise documents that it is ASIC-friendly and that dedicated Blake3
  miners exist. Those workloads remain outside TrMadenci rather than being presented as
  misleading GPU modes.
- AMD HIP on Windows supports only a bounded current device list. OpenCL is therefore
  the better first compatibility backend for older AMD mining rigs; HIP can later be an
  optimized backend for officially supported cards.

## Architecture required before P2

Introduce stable interfaces before adding a second algorithm:

- `IAlgorithm`: job decoding, header creation, target rules, result verification.
- `IComputeBackend`: CUDA, OpenCL, CPU and later HIP implementations.
- `IPoolProtocol`: Stratum V1 dialects and later Stratum V2/proxy transports.
- `CoinProfile`: ticker, algorithm parameters, address rules, pool presets and explorer
  metadata. The official developer destination is compiled in but always disclosed in
  UI and logs; it is never a hidden destination.
- `MiningSession`: cancellation on new jobs, nonce-space allocation, hashrate and share
  accounting independent of algorithm.

The fee scheduler must switch only between destinations that use the same coin,
algorithm, epoch/DAG and compatible pool protocol. It must never silently change the
user's selected algorithm or mine an unrelated coin.

## Definition of done for every algorithm

An algorithm is not listed as supported until all of these pass:

1. Published vectors pass in the CPU/reference path.
2. Optimized backend results match the reference across epochs/keys, boundary nonces
   and targets; CPU-only algorithms are not required to add a GPU implementation.
3. Accepted shares are observed on at least two independent pools or one pool plus a
   local reference node; submitted shares are CPU-verified first during qualification.
4. Reconnect, failover, difficulty changes, clean jobs, stale shares and cancellation
   are covered by automated tests.
5. A 24-hour multi-device soak test has no invalid shares, leaks or unrecovered worker
   errors.
6. VRAM/RAM requirements, supported devices, power behavior and measured performance
   are displayed rather than guessed.
7. Source and complete dependency licenses are recorded in third-party notices.

## Sources

- Ravencoin KAWPOW: https://ravencoin.org/about/
- Binance Pool algorithms and endpoints: https://www.binance.com/en/support/faq/detail/32843190fc1c4329a4df024339efa8d8
- Binance ETHW mining and exchange limitations: https://www.binance.com/de/support/faq/detail/a19bbb99a54a41cd9d49072d7fa7fd61
- Ethereum Classic miner FAQ: https://www.ethereumclassic.org/faqs/miners/
- Monero RandomX documentation: https://docs.getmonero.org/proof-of-work/random-x/
- RandomX reference and license: https://github.com/tevador/RandomX
- Evrmore algorithm differences: https://evrmorecoin.org/other/faq/
- Conflux Octopus specification: https://github.com/Conflux-Chain/CIPs/blob/master/CIPs/cip-3.md
- Zcash mining hardware guidance: https://zcash.readthedocs.io/en/master/rtd_pages/zcash_mining_guide.html
- Kaspa mining status: https://wiki.kaspa.org/mining
- Alephium ASIC policy: https://docs.alephium.org/frequently-asked-questions/
- OpenCL specification registry: https://registry.khronos.org/OpenCL/
- AMD HIP for Windows support matrix: https://rocm.docs.amd.com/projects/install-on-windows/en/latest/reference/system-requirements.html
- Stratum V2 mining protocol: https://stratumprotocol.org/specification/05-mining-protocol/
- Ethminer classic Stratum submission reference: https://github.com/ethereum-mining/ethminer/blob/master/libpoolprotocols/stratum/EthStratumClient.cpp
