# Third-party notices

## cpp-kawpow

- Source: https://github.com/RavenCommunity/cpp-kawpow
- Revision: `061d341011ca341e1f506c52b571f5fd64a0df71`
- License: Apache License 2.0
- Local copy: `third_party/cpp-kawpow`

The reference CPU implementation and its Keccak/Ethash primitives are used as a
correctness oracle for KAWPOW and ETCHash, and as reusable primitives in TrMadenci's
independently implemented Octopus light-cache oracle. They support test vectors, epoch
cache sizing, DAG sizing, CUDA cross-checks, and pre-submission share verification. The
original license is retained at `third_party/cpp-kawpow/LICENSE` and published as
`licenses/cpp-kawpow-LICENSE.txt`.

Local modifications: the unused `test/unittests/helpers.hpp` include was removed from
`lib/ethash/progpow.cpp`; it is referenced only by commented-out diagnostic code. The
epoch context was also parameterized with an explicit seed epoch and dataset-parent
count. Existing KAWPOW callers retain 512 parents; the ETCHash correctness path uses
the ECIP-1099 seed epoch and 256 parents. The changes remain under Apache-2.0.

## RandomX

- Source: https://github.com/tevador/RandomX
- Version: `v1.2.3`
- Revision: `12f2c2ffe2108d6cf54c391fee33c8bc3646cdab`
- License: BSD 3-Clause
- Local copy: `third_party/RandomX`

The unmodified RandomX v1 source is compiled as a private static dependency of
`TrMadenci.Native`. TrMadenci uses its portable light mode as a correctness oracle for
official Monero/RandomX vectors and exposes a persistent full shared-dataset/per-worker-VM
lifecycle with large-page fallback for the future CPU miner. Production mining remains
gated until the worker/session, developer-wallet route and live-share qualification are
complete. The upstream license is retained at
`third_party/RandomX/LICENSE` and published as `licenses/RandomX-LICENSE.txt`.

## NVIDIA CUDA Toolkit runtime components

- Components: `nvrtc64_130_0.dll`, `nvrtc-builtins64_131.dll`
- Copyright: NVIDIA Corporation
- License: NVIDIA CUDA Toolkit End User License Agreement
- License: https://docs.nvidia.com/cuda/eula/index.html

The Windows package places these unmodified runtime-compilation redistributables in
TrMadenci's private application directory. They remain subject to NVIDIA's license.

## Microsoft .NET runtime

- Source: https://github.com/dotnet/runtime
- License: MIT
- License text: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

The self-contained Windows package includes Microsoft .NET runtime binaries. .NET and
Microsoft are trademarks of their respective owners.
