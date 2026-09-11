// KAWPOW/Ethash dataset generation derived from the Apache-2.0 cpp-kawpow
// reference algorithm. CUDA implementation copyright TrMadenci contributors.

#include "trmadenci_native.h"
#include "cuda_epoch_context.hpp"
#include "octopus_reference.hpp"

#include "ethash-internal.hpp"

#include <cuda_runtime_api.h>
#include <cuda.h>

#include <algorithm>
#include <cstdint>
#include <chrono>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace
{
constexpr uint32_t fnv_prime = 0x01000193u;

__device__ __forceinline__ uint64_t rotate_left(const uint64_t value, const unsigned shift)
{
    return shift == 0 ? value : (value << shift) | (value >> (64 - shift));
}

__device__ void keccak_f1600(uint64_t state[25])
{
    constexpr uint64_t round_constants[24] = {
        0x0000000000000001ULL, 0x0000000000008082ULL, 0x800000000000808aULL,
        0x8000000080008000ULL, 0x000000000000808bULL, 0x0000000080000001ULL,
        0x8000000080008081ULL, 0x8000000000008009ULL, 0x000000000000008aULL,
        0x0000000000000088ULL, 0x0000000080008009ULL, 0x000000008000000aULL,
        0x000000008000808bULL, 0x800000000000008bULL, 0x8000000000008089ULL,
        0x8000000000008003ULL, 0x8000000000008002ULL, 0x8000000000000080ULL,
        0x000000000000800aULL, 0x800000008000000aULL, 0x8000000080008081ULL,
        0x8000000000008080ULL, 0x0000000080000001ULL, 0x8000000080008008ULL};
    constexpr unsigned rho[25] = {
        0, 1, 62, 28, 27,
        36, 44, 6, 55, 20,
        3, 10, 43, 25, 39,
        41, 45, 15, 21, 8,
        18, 2, 61, 56, 14};

    for (const auto round_constant : round_constants)
    {
        uint64_t column[5];
        uint64_t delta[5];
        uint64_t rotated[25];

#pragma unroll
        for (int x = 0; x < 5; ++x)
            column[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
#pragma unroll
        for (int x = 0; x < 5; ++x)
            delta[x] = column[(x + 4) % 5] ^ rotate_left(column[(x + 1) % 5], 1);
#pragma unroll
        for (int y = 0; y < 5; ++y)
#pragma unroll
            for (int x = 0; x < 5; ++x)
                state[x + 5 * y] ^= delta[x];

#pragma unroll
        for (int y = 0; y < 5; ++y)
#pragma unroll
            for (int x = 0; x < 5; ++x)
                rotated[y + 5 * ((2 * x + 3 * y) % 5)] =
                    rotate_left(state[x + 5 * y], rho[x + 5 * y]);

#pragma unroll
        for (int y = 0; y < 5; ++y)
#pragma unroll
            for (int x = 0; x < 5; ++x)
                state[x + 5 * y] = rotated[x + 5 * y] ^
                    ((~rotated[(x + 1) % 5 + 5 * y]) & rotated[(x + 2) % 5 + 5 * y]);

        state[0] ^= round_constant;
    }
}

__device__ void keccak512_64(uint32_t words[16])
{
    uint64_t state[25]{};
#pragma unroll
    for (int i = 0; i < 8; ++i)
        state[i] = static_cast<uint64_t>(words[i * 2]) |
            (static_cast<uint64_t>(words[i * 2 + 1]) << 32);
    state[8] = 0x8000000000000001ULL;
    keccak_f1600(state);
#pragma unroll
    for (int i = 0; i < 8; ++i)
    {
        words[i * 2] = static_cast<uint32_t>(state[i]);
        words[i * 2 + 1] = static_cast<uint32_t>(state[i] >> 32);
    }
}

__global__ void generate_dataset_nodes(
    const uint32_t* light_cache,
    const uint32_t light_cache_items,
    uint32_t* output,
    const uint32_t start_node,
    const uint32_t node_count,
    const uint32_t dataset_parents)
{
    const auto local_index = blockIdx.x * blockDim.x + threadIdx.x;
    if (local_index >= node_count)
        return;
    const auto node_index = start_node + local_index;

    uint32_t mix[16];
    const auto source = (node_index % light_cache_items) * 16;
#pragma unroll
    for (int word = 0; word < 16; ++word)
        mix[word] = light_cache[source + word];

    mix[0] ^= node_index;
    keccak512_64(mix);

#pragma unroll 1
    for (uint32_t round = 0; round < dataset_parents; ++round)
    {
        const auto parent = (((node_index ^ round) * fnv_prime) ^ mix[round % 16]) % light_cache_items;
        const auto parent_offset = parent * 16;
#pragma unroll
        for (int word = 0; word < 16; ++word)
            mix[word] = (mix[word] * fnv_prime) ^ light_cache[parent_offset + word];
    }

    keccak512_64(mix);
    const auto destination = node_index * 16;
#pragma unroll
    for (int word = 0; word < 16; ++word)
        output[destination + word] = mix[word];
}

int32_t cuda_failure(const cudaError_t status)
{
    return status == cudaSuccess ? 0 : -1000 - static_cast<int32_t>(status);
}

cudaError_t launch_dataset_batches(
    const uint32_t* device_light,
    const uint32_t light_items,
    uint32_t* device_output,
    const uint32_t total_nodes,
    const uint32_t dataset_parents)
{
    constexpr uint32_t threads = 64;
    constexpr uint32_t nodes_per_batch = 131072;

    for (uint32_t start = 0; start < total_nodes; start += nodes_per_batch)
    {
        const auto count = std::min(nodes_per_batch, total_nodes - start);
        const auto blocks = (count + threads - 1) / threads;
        generate_dataset_nodes<<<blocks, threads>>>(
            device_light, light_items, device_output, start, count, dataset_parents);
        auto status = cudaGetLastError();
        if (status != cudaSuccess)
            return status;
        status = cudaDeviceSynchronize();
        if (status != cudaSuccess)
            return status;
    }
    return cudaSuccess;
}
}

int32_t trmadenci_validate_cuda_dag_items(
    const int32_t block_number,
    const uint32_t item_count,
    uint32_t* first_mismatch)
{
    if (block_number < 0 || item_count == 0 || first_mismatch == nullptr ||
        item_count > std::numeric_limits<uint32_t>::max() / 2)
        return -1;

    *first_mismatch = std::numeric_limits<uint32_t>::max();
    const auto context = ethash::create_epoch_context(ethash::get_epoch_number(block_number));
    if (!context)
        return -2;

    const auto light_bytes = ethash::get_light_cache_size(context->light_cache_num_items);
    const auto output_bytes = static_cast<size_t>(item_count) * sizeof(ethash::hash1024);
    uint32_t* device_light = nullptr;
    uint32_t* device_output = nullptr;

    auto status = cudaMalloc(&device_light, light_bytes);
    if (status != cudaSuccess)
        return cuda_failure(status);
    status = cudaMalloc(&device_output, output_bytes);
    if (status != cudaSuccess)
    {
        cudaFree(device_light);
        return cuda_failure(status);
    }

    status = cudaMemcpy(device_light, context->light_cache, light_bytes, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
    {
        const auto node_count = item_count * 2;
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->light_cache_num_items),
            device_output,
            node_count,
            512);
    }

    std::vector<ethash::hash1024> gpu_items(item_count);
    if (status == cudaSuccess)
        status = cudaMemcpy(gpu_items.data(), device_output, output_bytes, cudaMemcpyDeviceToHost);

    cudaFree(device_output);
    cudaFree(device_light);
    if (status != cudaSuccess)
        return cuda_failure(status);

    for (uint32_t index = 0; index < item_count; ++index)
    {
        const auto cpu_item = ethash::calculate_dataset_item_1024(*context, index);
        if (std::memcmp(cpu_item.bytes, gpu_items[index].bytes, sizeof(cpu_item.bytes)) != 0)
        {
            *first_mismatch = index;
            return 1;
        }
    }
    return 0;
}

int32_t trmadenci_validate_etchash_cuda_dag_items(
    const int32_t block_number,
    const uint32_t item_count,
    uint32_t* first_mismatch)
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;
    constexpr int32_t etchash_dataset_parents = 256;

    if (block_number < 0 || item_count == 0 || first_mismatch == nullptr ||
        item_count > std::numeric_limits<uint32_t>::max() / 2)
        return -1;

    *first_mismatch = std::numeric_limits<uint32_t>::max();
    const auto epoch_length =
        block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    ethash::epoch_context_ptr context{
        ethash_create_epoch_context_configured(
            dataset_epoch, seed_epoch, etchash_dataset_parents),
        ethash_destroy_epoch_context};
    if (!context)
        return -2;

    const auto light_bytes = ethash::get_light_cache_size(context->light_cache_num_items);
    const auto output_bytes = static_cast<size_t>(item_count) * sizeof(ethash::hash1024);
    uint32_t* device_light = nullptr;
    uint32_t* device_output = nullptr;

    auto status = cudaMalloc(&device_light, light_bytes);
    if (status != cudaSuccess)
        return cuda_failure(status);
    status = cudaMalloc(&device_output, output_bytes);
    if (status != cudaSuccess)
    {
        cudaFree(device_light);
        return cuda_failure(status);
    }

    status = cudaMemcpy(device_light, context->light_cache, light_bytes, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
    {
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->light_cache_num_items),
            device_output,
            item_count * 2,
            etchash_dataset_parents);
    }

    std::vector<ethash::hash1024> gpu_items(item_count);
    if (status == cudaSuccess)
        status = cudaMemcpy(gpu_items.data(), device_output, output_bytes, cudaMemcpyDeviceToHost);
    cudaFree(device_output);
    cudaFree(device_light);
    if (status != cudaSuccess)
        return cuda_failure(status);

    for (uint32_t index = 0; index < item_count; ++index)
    {
        const auto cpu_item = ethash::calculate_dataset_item_1024(*context, index);
        if (std::memcmp(cpu_item.bytes, gpu_items[index].bytes, sizeof(cpu_item.bytes)) != 0)
        {
            *first_mismatch = index;
            return 1;
        }
    }
    return 0;
}

int32_t trmadenci_validate_octopus_cuda_dag_items(
    const uint64_t block_number,
    const uint32_t item_count,
    uint32_t* first_mismatch)
{
    if (item_count == 0 || first_mismatch == nullptr ||
        item_count > std::numeric_limits<uint32_t>::max() / 4)
        return -1;

    *first_mismatch = std::numeric_limits<uint32_t>::max();
    const auto context = trmadenci_create_octopus_light_context(block_number);
    if (!context)
        return -2;

    const auto light_bytes = context->cache.size() * sizeof(ethash::hash512);
    const auto output_bytes = static_cast<size_t>(item_count) * sizeof(ethash::hash2048);
    uint32_t* device_light = nullptr;
    uint32_t* device_output = nullptr;
    auto status = cudaMalloc(&device_light, light_bytes);
    if (status == cudaSuccess)
        status = cudaMalloc(&device_output, output_bytes);
    if (status == cudaSuccess)
        status = cudaMemcpy(
            device_light, context->cache.data(), light_bytes, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
    {
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->cache.size()),
            device_output,
            item_count * 4,
            256);
    }

    std::vector<ethash::hash2048> gpu_items(item_count);
    if (status == cudaSuccess)
        status = cudaMemcpy(gpu_items.data(), device_output, output_bytes, cudaMemcpyDeviceToHost);
    if (device_output != nullptr) cudaFree(device_output);
    if (device_light != nullptr) cudaFree(device_light);
    if (status != cudaSuccess)
        return cuda_failure(status);

    for (uint32_t index = 0; index < item_count; ++index)
    {
        const auto cpu_item = ethash::calculate_dataset_item_2048(*context->ethash_context, index);
        if (std::memcmp(cpu_item.bytes, gpu_items[index].bytes, sizeof(cpu_item.bytes)) != 0)
        {
            *first_mismatch = index;
            return 1;
        }
    }
    return 0;
}

int32_t trmadenci_create_cuda_epoch(
    const int32_t block_number,
    const int32_t device_index,
    trmadenci_cuda_epoch** epoch,
    trmadenci_cuda_epoch_build_info* build_info)
{
    if (block_number < 0 || device_index < 0 || epoch == nullptr || build_info == nullptr)
        return -1;
    *epoch = nullptr;
    *build_info = {};

    auto status = cudaSetDevice(device_index);
    if (status != cudaSuccess)
        return cuda_failure(status);

    const auto epoch_number = ethash::get_epoch_number(block_number);
    const auto context = ethash::create_epoch_context(epoch_number);
    if (!context)
        return -2;

    const auto light_bytes = ethash::get_light_cache_size(context->light_cache_num_items);
    const auto dataset_bytes = ethash::get_full_dataset_size(context->full_dataset_num_items);
    const auto total_nodes_64 = static_cast<uint64_t>(context->full_dataset_num_items) * 2;
    if (total_nodes_64 > std::numeric_limits<uint32_t>::max())
        return -3;

    size_t free_bytes = 0;
    size_t total_bytes = 0;
    status = cudaMemGetInfo(&free_bytes, &total_bytes);
    constexpr size_t safety_margin = 256ULL * 1024 * 1024;
    if (status != cudaSuccess)
        return cuda_failure(status);
    if (dataset_bytes + light_bytes + safety_margin > free_bytes)
        return -4;

    uint32_t* device_light = nullptr;
    uint32_t* device_dataset = nullptr;
    status = cudaMalloc(&device_light, light_bytes);
    if (status == cudaSuccess)
        status = cudaMalloc(&device_dataset, static_cast<size_t>(dataset_bytes));
    if (status == cudaSuccess)
        status = cudaMemcpy(device_light, context->light_cache, light_bytes, cudaMemcpyHostToDevice);

    const auto started = std::chrono::steady_clock::now();
    if (status == cudaSuccess)
    {
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->light_cache_num_items),
            device_dataset,
            static_cast<uint32_t>(total_nodes_64),
            512);
    }
    const auto finished = std::chrono::steady_clock::now();
    cudaFree(device_light);

    if (status != cudaSuccess)
    {
        if (device_dataset != nullptr)
            cudaFree(device_dataset);
        return cuda_failure(status);
    }

    auto* result = new (std::nothrow) trmadenci_cuda_epoch{
        device_index,
        epoch_number,
        1,
        static_cast<uint32_t>(context->full_dataset_num_items),
        dataset_bytes,
        device_dataset,
        -1,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        0,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr};
    if (result == nullptr)
    {
        cudaFree(device_dataset);
        return -5;
    }

    build_info->epoch_number = epoch_number;
    build_info->device_index = device_index;
    build_info->dataset_bytes = dataset_bytes;
    build_info->build_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    *epoch = result;
    return 0;
}

int32_t trmadenci_create_etchash_cuda_epoch(
    const int32_t block_number,
    const int32_t device_index,
    trmadenci_cuda_epoch** epoch,
    trmadenci_cuda_epoch_build_info* build_info)
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;
    constexpr int32_t etchash_dataset_parents = 256;

    if (block_number < 0 || device_index < 0 || epoch == nullptr || build_info == nullptr)
        return -1;
    *epoch = nullptr;
    *build_info = {};

    auto status = cudaSetDevice(device_index);
    if (status != cudaSuccess)
        return cuda_failure(status);

    const auto epoch_length =
        block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    ethash::epoch_context_ptr context{
        ethash_create_epoch_context_configured(
            dataset_epoch, seed_epoch, etchash_dataset_parents),
        ethash_destroy_epoch_context};
    if (!context)
        return -2;

    const auto light_bytes = ethash::get_light_cache_size(context->light_cache_num_items);
    const auto dataset_bytes = ethash::get_full_dataset_size(context->full_dataset_num_items);
    const auto total_nodes_64 = static_cast<uint64_t>(context->full_dataset_num_items) * 2;
    if (total_nodes_64 > std::numeric_limits<uint32_t>::max())
        return -3;

    size_t free_bytes = 0;
    size_t total_bytes = 0;
    status = cudaMemGetInfo(&free_bytes, &total_bytes);
    constexpr size_t safety_margin = 256ULL * 1024 * 1024;
    if (status != cudaSuccess)
        return cuda_failure(status);
    if (dataset_bytes + light_bytes + safety_margin > free_bytes)
        return -4;

    uint32_t* device_light = nullptr;
    uint32_t* device_dataset = nullptr;
    status = cudaMalloc(&device_light, light_bytes);
    if (status == cudaSuccess)
        status = cudaMalloc(&device_dataset, static_cast<size_t>(dataset_bytes));
    if (status == cudaSuccess)
        status = cudaMemcpy(device_light, context->light_cache, light_bytes, cudaMemcpyHostToDevice);

    const auto started = std::chrono::steady_clock::now();
    if (status == cudaSuccess)
    {
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->light_cache_num_items),
            device_dataset,
            static_cast<uint32_t>(total_nodes_64),
            etchash_dataset_parents);
    }
    const auto finished = std::chrono::steady_clock::now();
    cudaFree(device_light);

    if (status != cudaSuccess)
    {
        if (device_dataset != nullptr)
            cudaFree(device_dataset);
        return cuda_failure(status);
    }

    auto* result = new (std::nothrow) trmadenci_cuda_epoch{
        device_index,
        dataset_epoch,
        2,
        static_cast<uint32_t>(context->full_dataset_num_items),
        dataset_bytes,
        device_dataset,
        -1,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        0,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr};
    if (result == nullptr)
    {
        cudaFree(device_dataset);
        return -5;
    }

    build_info->epoch_number = dataset_epoch;
    build_info->device_index = device_index;
    build_info->dataset_bytes = dataset_bytes;
    build_info->build_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    *epoch = result;
    return 0;
}

int32_t trmadenci_create_octopus_cuda_epoch(
    const uint64_t block_number,
    const int32_t device_index,
    trmadenci_cuda_epoch** epoch,
    trmadenci_cuda_epoch_build_info* build_info)
{
    if (device_index < 0 || epoch == nullptr || build_info == nullptr)
        return -1;
    *epoch = nullptr;
    *build_info = {};

    auto status = cudaSetDevice(device_index);
    if (status != cudaSuccess)
        return cuda_failure(status);
    const auto context = trmadenci_create_octopus_light_context(block_number);
    if (!context)
        return -2;
    const auto total_nodes = context->dataset_bytes / sizeof(ethash::hash512);
    if (total_nodes > std::numeric_limits<uint32_t>::max())
        return -3;

    const auto light_bytes = context->cache.size() * sizeof(ethash::hash512);
    size_t free_bytes = 0;
    size_t total_bytes = 0;
    status = cudaMemGetInfo(&free_bytes, &total_bytes);
    constexpr size_t safety_margin = 512ULL * 1024 * 1024;
    if (status != cudaSuccess)
        return cuda_failure(status);
    if (context->dataset_bytes + light_bytes + safety_margin > free_bytes)
        return -4;

    uint32_t* device_light = nullptr;
    uint32_t* device_dataset = nullptr;
    status = cudaMalloc(&device_light, light_bytes);
    if (status == cudaSuccess)
        status = cudaMalloc(&device_dataset, static_cast<size_t>(context->dataset_bytes));
    if (status == cudaSuccess)
        status = cudaMemcpy(
            device_light, context->cache.data(), light_bytes, cudaMemcpyHostToDevice);

    const auto started = std::chrono::steady_clock::now();
    if (status == cudaSuccess)
    {
        status = launch_dataset_batches(
            device_light,
            static_cast<uint32_t>(context->cache.size()),
            device_dataset,
            static_cast<uint32_t>(total_nodes),
            256);
    }
    const auto finished = std::chrono::steady_clock::now();
    if (device_light != nullptr) cudaFree(device_light);
    if (status != cudaSuccess)
    {
        if (device_dataset != nullptr) cudaFree(device_dataset);
        return cuda_failure(status);
    }

    auto* result = new (std::nothrow) trmadenci_cuda_epoch{
        device_index,
        context->epoch,
        3,
        static_cast<uint32_t>(context->dataset_bytes / sizeof(ethash::hash2048)),
        context->dataset_bytes,
        device_dataset,
        -1,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        0,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr};
    if (result == nullptr)
    {
        cudaFree(device_dataset);
        return -5;
    }

    build_info->epoch_number = context->epoch;
    build_info->device_index = device_index;
    build_info->dataset_bytes = context->dataset_bytes;
    build_info->build_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    *epoch = result;
    return 0;
}

void trmadenci_destroy_cuda_epoch(trmadenci_cuda_epoch* epoch)
{
    if (epoch == nullptr)
        return;
    cudaSetDevice(epoch->device_index);
    if (epoch->jit_module != nullptr)
        cuModuleUnload(reinterpret_cast<CUmodule>(epoch->jit_module));
    if (epoch->search_result != nullptr) cudaFree(epoch->search_result);
    if (epoch->search_reduced != nullptr) cudaFree(epoch->search_reduced);
    if (epoch->search_initial != nullptr) cudaFree(epoch->search_initial);
    if (epoch->search_target != nullptr) cudaFree(epoch->search_target);
    if (epoch->search_header != nullptr) cudaFree(epoch->search_header);
    cudaFree(epoch->dataset);
    delete epoch;
}
