#include "trmadenci_native.h"

#include <ethash/ethash.hpp>
#include <ethash/progpow.hpp>

#include <cstring>
#include <mutex>

namespace
{
ethash::epoch_context_ptr cached_context{nullptr, ethash_destroy_epoch_context};
ethash::epoch_context_ptr cached_etchash_context{nullptr, ethash_destroy_epoch_context};
std::mutex cached_context_mutex;
}

int32_t trmadenci_get_epoch_info(const int32_t block_number, trmadenci_epoch_info* info)
{
    if (block_number < 0 || info == nullptr)
        return -1;

    const auto epoch = ethash::get_epoch_number(block_number);
    const auto light_items = ethash::calculate_light_cache_num_items(epoch);
    const auto dataset_items = ethash::calculate_full_dataset_num_items(epoch);

    info->epoch_number = epoch;
    info->light_cache_bytes = ethash::get_light_cache_size(light_items);
    info->full_dataset_bytes = ethash::get_full_dataset_size(dataset_items);
    return 0;
}

int32_t trmadenci_etchash_get_epoch_info(
    const int32_t block_number,
    trmadenci_etchash_epoch_info* info)
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;

    if (block_number < 0 || info == nullptr)
        return -1;

    const auto epoch_length =
        block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    const auto light_items = ethash::calculate_light_cache_num_items(dataset_epoch);
    const auto dataset_items = ethash::calculate_full_dataset_num_items(dataset_epoch);
    const auto seed = ethash::calculate_epoch_seed(seed_epoch);

    info->dataset_epoch_number = dataset_epoch;
    info->seed_epoch_number = seed_epoch;
    info->light_cache_bytes = ethash::get_light_cache_size(light_items);
    info->full_dataset_bytes = ethash::get_full_dataset_size(dataset_items);
    std::memcpy(info->seed_hash, seed.bytes, sizeof(info->seed_hash));
    return 0;
}

int32_t trmadenci_etchash_find_seed_epoch(
    const uint8_t seed_hash[32],
    int32_t* seed_epoch_number)
{
    if (seed_hash == nullptr || seed_epoch_number == nullptr)
        return -1;
    ethash::hash256 seed{};
    std::memcpy(seed.bytes, seed_hash, sizeof(seed.bytes));
    const auto epoch = ethash::find_epoch_number(seed);
    if (epoch < 0)
        return -2;
    *seed_epoch_number = epoch;
    return 0;
}

int32_t trmadenci_kawpow_hash_reference(
    const int32_t block_number,
    const uint8_t header_hash[32],
    const uint64_t nonce,
    uint8_t mix_hash[32],
    uint8_t final_hash[32])
{
    if (block_number < 0 || header_hash == nullptr || mix_hash == nullptr || final_hash == nullptr)
        return -1;

    const std::lock_guard lock{cached_context_mutex};
    const auto epoch = ethash::get_epoch_number(block_number);
    if (!cached_context || cached_context->epoch_number != epoch)
        cached_context = ethash::create_epoch_context(epoch);
    if (!cached_context)
        return -2;

    ethash::hash256 header{};
    std::memcpy(header.bytes, header_hash, sizeof(header.bytes));
    const auto result = progpow::hash(*cached_context, block_number, header, nonce);
    std::memcpy(mix_hash, result.mix_hash.bytes, sizeof(result.mix_hash.bytes));
    std::memcpy(final_hash, result.final_hash.bytes, sizeof(result.final_hash.bytes));
    return 0;
}

int32_t trmadenci_etchash_hash_reference(
    const int32_t block_number,
    const uint8_t header_hash[32],
    const uint64_t nonce,
    uint8_t mix_hash[32],
    uint8_t final_hash[32])
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;
    constexpr int32_t etchash_dataset_parents = 256;

    if (block_number < 0 || header_hash == nullptr || mix_hash == nullptr || final_hash == nullptr)
        return -1;

    const std::lock_guard lock{cached_context_mutex};
    const auto epoch_length =
        block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    if (!cached_etchash_context || cached_etchash_context->epoch_number != dataset_epoch)
    {
        cached_etchash_context.reset(ethash_create_epoch_context_configured(
            dataset_epoch, seed_epoch, etchash_dataset_parents));
    }
    if (!cached_etchash_context)
        return -2;

    ethash::hash256 header{};
    std::memcpy(header.bytes, header_hash, sizeof(header.bytes));
    const auto result = ethash::hash(*cached_etchash_context, header, nonce);
    std::memcpy(mix_hash, result.mix_hash.bytes, sizeof(result.mix_hash.bytes));
    std::memcpy(final_hash, result.final_hash.bytes, sizeof(result.final_hash.bytes));
    return 0;
}
