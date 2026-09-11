#include "trmadenci_native.h"

#include <ethash/progpow.hpp>

#include "progpow_test_vectors.hpp"

#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>

namespace
{
bool equals_hex(const uint8_t* bytes, const char* hex)
{
    constexpr char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < 32; ++i)
    {
        if (digits[bytes[i] >> 4] != hex[i * 2] || digits[bytes[i] & 0x0f] != hex[i * 2 + 1])
            return false;
    }
    return true;
}

ethash::hash256 hash_from_hex(const char* hex)
{
    auto digit = [](const char value) -> uint8_t
    {
        return static_cast<uint8_t>(value <= '9' ? value - '0' : value - 'a' + 10);
    };

    ethash::hash256 result{};
    for (size_t i = 0; i < sizeof(result.bytes); ++i)
        result.bytes[i] = static_cast<uint8_t>((digit(hex[i * 2]) << 4) | digit(hex[i * 2 + 1]));
    return result;
}
}

int main()
{
    trmadenci_etchash_epoch_info etchash_epoch{};
    if (trmadenci_etchash_get_epoch_info(11'700'000, &etchash_epoch) != 0 ||
        etchash_epoch.dataset_epoch_number != 195 || etchash_epoch.seed_epoch_number != 390 ||
        !equals_hex(
            etchash_epoch.seed_hash,
            "e79f0f63030bf691445c2b9d0266b24a9619e355194067f2ad2c73a8e0a26c65"))
    {
        std::cerr << "ETCHash ECIP-1099 activation vector mismatch.\n";
        return EXIT_FAILURE;
    }

    const auto count = trmadenci_get_device_count();
    if (count <= 0)
    {
        std::cerr << "No CUDA device: " << trmadenci_get_last_error() << '\n';
        return EXIT_FAILURE;
    }

    trmadenci_device_info info{};
    if (trmadenci_get_device_info(0, &info) != 0)
    {
        std::cerr << "Device query failed: " << trmadenci_get_last_error() << '\n';
        return EXIT_FAILURE;
    }

    std::cout << "GPU0: " << info.name << ", "
              << (info.total_memory_bytes / 1024 / 1024) << " MiB, sm_"
              << info.compute_major << info.compute_minor << '\n';

    trmadenci_gpu_telemetry telemetry{};
    if (trmadenci_get_gpu_telemetry(0, &telemetry) != 0 || telemetry.valid_fields == 0)
    {
        std::cerr << "NVML telemetry query failed.\n";
        return EXIT_FAILURE;
    }
    if ((telemetry.valid_fields & TRMADENCI_TELEMETRY_MEMORY) != 0 &&
        (telemetry.memory_total_bytes == 0 || telemetry.memory_used_bytes > telemetry.memory_total_bytes))
    {
        std::cerr << "NVML returned invalid memory telemetry.\n";
        return EXIT_FAILURE;
    }
    std::cout << "GPU0 telemetry fields: 0x" << std::hex << telemetry.valid_fields << std::dec << '\n';

    uint32_t etchash_first_mismatch = 0;
    if (trmadenci_validate_etchash_cuda_dag_items(
            11'700'000, 8, &etchash_first_mismatch) != 0)
    {
        std::cerr << "ETCHash CUDA DAG validation failed at item "
                  << etchash_first_mismatch << ".\n";
        return EXIT_FAILURE;
    }
    std::cout << "8 ETCHash CUDA DAG items match the 256-parent CPU reference.\n";

    trmadenci_epoch_info epoch{};
    if (trmadenci_get_epoch_info(0, &epoch) != 0 || epoch.epoch_number != 0 ||
        epoch.light_cache_bytes == 0 || epoch.full_dataset_bytes == 0)
    {
        std::cerr << "Epoch sizing failed.\n";
        return EXIT_FAILURE;
    }

    uint8_t header[32]{};
    uint8_t mix[32]{};
    uint8_t final_hash[32]{};
    if (trmadenci_kawpow_hash_reference(0, header, 0, mix, final_hash) != 0)
    {
        std::cerr << "KAWPOW reference hash failed.\n";
        return EXIT_FAILURE;
    }

    if (!equals_hex(mix, "6e97b47b134fda0c7888802988e1a373affeb28bcd813b6e9a0fc669c935d03a") ||
        !equals_hex(final_hash, "e601a7257a70dc48fccc97a7330d704d776047623b92883d77111fb36870f3d1"))
    {
        std::cerr << "KAWPOW reference vector mismatch.\n";
        return EXIT_FAILURE;
    }

    std::cout << "KAWPOW vector #0 verified; epoch cache=" << epoch.light_cache_bytes
              << " bytes, DAG=" << epoch.full_dataset_bytes << " bytes\n";

    ethash::epoch_context_ptr context{nullptr, ethash_destroy_epoch_context};
    size_t verified_vectors = 0;
    for (const auto& vector : progpow_hash_test_cases)
    {
        const auto vector_epoch = ethash::get_epoch_number(vector.block_number);
        if (!context || context->epoch_number != vector_epoch)
            context = ethash::create_epoch_context(vector_epoch);

        const auto result = progpow::hash(
            *context,
            vector.block_number,
            hash_from_hex(vector.header_hash_hex),
            std::stoull(vector.nonce_hex, nullptr, 16));

        if (!equals_hex(result.mix_hash.bytes, vector.mix_hash_hex) ||
            !equals_hex(result.final_hash.bytes, vector.final_hash_hex))
        {
            std::cerr << "KAWPOW vector mismatch at block " << vector.block_number << "\n";
            return EXIT_FAILURE;
        }
        ++verified_vectors;
    }

    std::cout << verified_vectors << " official KAWPOW vectors verified.\n";

    uint32_t first_mismatch = 0;
    const auto dag_status = trmadenci_validate_cuda_dag_items(0, 8, &first_mismatch);
    if (dag_status != 0)
    {
        std::cerr << "CUDA DAG validation failed with status " << dag_status
                  << ", first mismatch " << first_mismatch << "\n";
        return EXIT_FAILURE;
    }
    std::cout << "8 CUDA DAG items match the CPU reference byte-for-byte.\n";

    trmadenci_cuda_epoch* cuda_epoch = nullptr;
    trmadenci_cuda_epoch_build_info build_info{};
    const auto build_status = trmadenci_create_cuda_epoch(0, 0, &cuda_epoch, &build_info);
    if (build_status != 0)
    {
        std::cerr << "CUDA epoch 0 build failed with status " << build_status << "\n";
        return EXIT_FAILURE;
    }

    uint8_t easy_target[32];
    std::memset(easy_target, 0xff, sizeof(easy_target));
    trmadenci_search_result search_result{};
    const auto search_status = trmadenci_search_cuda(
        cuda_epoch, 0, header, easy_target, 0, 1, &search_result);
    if (search_status != 0 || search_result.solution_found == 0 || search_result.nonce != 0 ||
        !equals_hex(search_result.mix_hash, "6e97b47b134fda0c7888802988e1a373affeb28bcd813b6e9a0fc669c935d03a") ||
        !equals_hex(search_result.final_hash, "e601a7257a70dc48fccc97a7330d704d776047623b92883d77111fb36870f3d1"))
    {
        std::cerr << "CUDA nonce kernel does not match official vector #0.\n";
        trmadenci_destroy_cuda_epoch(cuda_epoch);
        return EXIT_FAILURE;
    }
    std::cout << "CUDA nonce 0 matches official vector #0; DAG build "
              << build_info.build_milliseconds / 1000.0 << "s, search "
              << search_result.search_milliseconds << "ms.\n";

    const auto second_header = hash_from_hex(progpow_hash_test_cases[1].header_hash_hex);
    trmadenci_search_result second_result{};
    const auto second_nonce = std::stoull(progpow_hash_test_cases[1].nonce_hex, nullptr, 16);
    const auto second_status = trmadenci_search_cuda(
        cuda_epoch,
        progpow_hash_test_cases[1].block_number,
        second_header.bytes,
        easy_target,
        second_nonce,
        1,
        &second_result);
    if (second_status != 0 || second_result.solution_found == 0 ||
        second_result.nonce != second_nonce ||
        !equals_hex(second_result.mix_hash, progpow_hash_test_cases[1].mix_hash_hex) ||
        !equals_hex(second_result.final_hash, progpow_hash_test_cases[1].final_hash_hex))
    {
        std::cerr << "CUDA JIT program does not match official vector #1.\n";
        trmadenci_destroy_cuda_epoch(cuda_epoch);
        return EXIT_FAILURE;
    }
    std::cout << "CUDA JIT program also matches official vector #1.\n";

    uint8_t impossible_target[32]{};
    trmadenci_search_result benchmark_result{};
    constexpr uint32_t benchmark_hashes = 65536;
    const auto benchmark_status = trmadenci_search_cuda(
        cuda_epoch, 0, header, impossible_target, 1, benchmark_hashes, &benchmark_result);
    trmadenci_destroy_cuda_epoch(cuda_epoch);
    if (benchmark_status != 0 || benchmark_result.solution_found != 0 ||
        benchmark_result.hashes_searched != benchmark_hashes ||
        benchmark_result.search_milliseconds <= 0)
    {
        std::cerr << "CUDA nonce benchmark failed.\n";
        return EXIT_FAILURE;
    }
    std::cout << "CUDA lane benchmark: "
              << benchmark_hashes / (benchmark_result.search_milliseconds / 1000.0)
              << " H/s.\n";

    trmadenci_cuda_epoch* etchash_cuda_epoch = nullptr;
    trmadenci_cuda_epoch_build_info etchash_build_info{};
    if (trmadenci_create_etchash_cuda_epoch(
            22, 0, &etchash_cuda_epoch, &etchash_build_info) != 0)
    {
        std::cerr << "ETCHash CUDA epoch 0 build failed.\n";
        return EXIT_FAILURE;
    }
    const auto etchash_header =
        hash_from_hex("372eca2454ead349c3df0ab5d00b0b706b23e49d469387db91811cee0358fc6d");
    trmadenci_search_result etchash_result{};
    constexpr uint64_t etchash_nonce = 0x495732e0ed7a801cULL;
    const auto etchash_search_status = trmadenci_search_etchash_cuda(
        etchash_cuda_epoch,
        22,
        etchash_header.bytes,
        easy_target,
        etchash_nonce,
        1,
        &etchash_result);
    trmadenci_destroy_cuda_epoch(etchash_cuda_epoch);
    if (etchash_search_status != 0 || etchash_result.solution_found == 0 ||
        etchash_result.nonce != etchash_nonce ||
        !equals_hex(
            etchash_result.final_hash,
            "00000b184f1fdd88bfd94c86c39e65db0c36144d5e43f745f722196e730cb614"))
    {
        std::cerr << "ETCHash CUDA nonce vector mismatch.\n";
        return EXIT_FAILURE;
    }
    std::cout << "ETCHash CUDA nonce matches the official epoch-0 vector.\n";
    return EXIT_SUCCESS;
}
