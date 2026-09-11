#include "trmadenci_native.h"
#include "octopus_reference.hpp"

#include <ethash/keccak.hpp>

#include <array>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <vector>

namespace
{
constexpr uint64_t epoch_length = uint64_t{1} << 19;
constexpr uint64_t cache_init = uint64_t{1} << 24;
constexpr uint64_t cache_growth = uint64_t{1} << 16;
constexpr uint64_t dataset_init = uint64_t{1} << 32;
constexpr uint64_t dataset_growth = uint64_t{1} << 24;
constexpr uint32_t dataset_parents = 256;
constexpr uint32_t accesses = 32;
constexpr uint32_t mix_words = 64;
constexpr uint32_t fnv_prime = 0x01000193;

bool is_prime(const uint64_t value) noexcept
{
    if (value < 2)
        return false;
    if ((value & 1) == 0)
        return value == 2;
    for (uint64_t divisor = 3; divisor <= value / divisor; divisor += 2)
    {
        if (value % divisor == 0)
            return false;
    }
    return true;
}

uint64_t prime_sized_bytes(
    const uint64_t initial,
    const uint64_t growth,
    const uint64_t item_size,
    const uint64_t epoch) noexcept
{
    auto size = initial + growth * epoch - item_size;
    while (!is_prime(size / item_size))
        size -= 2 * item_size;
    return size;
}

constexpr uint32_t fnv1(const uint32_t left, const uint32_t right) noexcept
{
    return left * fnv_prime ^ right;
}

std::unique_ptr<trmadenci_octopus_light_context> cached_context;
std::mutex context_mutex;

ethash::hash256 hash(
    const trmadenci_octopus_light_context& context,
    const uint8_t header_hash[32],
    const uint64_t compressed_multi_point,
    const uint32_t points[accesses]) noexcept
{
    std::array<uint8_t, 96> final_input{};
    std::memcpy(final_input.data(), header_hash, 32);
    std::memcpy(final_input.data() + 32, &compressed_multi_point, sizeof(compressed_multi_point));
    const auto seed = ethash::keccak512(final_input.data(), 40);

    std::array<uint32_t, mix_words> mix{};
    for (uint32_t i = 0; i < mix_words; ++i)
        mix[i] = seed.word32s[i % 16];

    const auto num_pages = static_cast<uint32_t>(context.dataset_bytes / 256);
    for (uint32_t access = 0; access < accesses; ++access)
    {
        const auto page = fnv1(seed.word32s[0] ^ access ^ points[access], mix[access % mix_words]) %
            num_pages;
        for (uint32_t node = 0; node < 4; ++node)
        {
            const auto dataset_item =
                ethash::calculate_dataset_item_512(*context.ethash_context, uint64_t{page} * 4 + node);
            for (uint32_t word = 0; word < 16; ++word)
            {
                const auto index = node * 16 + word;
                mix[index] = fnv1(mix[index], dataset_item.word32s[word]);
            }
        }
    }

    for (uint32_t word = 0; word < mix_words; word += 4)
    {
        auto reduction = fnv1(mix[word], mix[word + 1]);
        reduction = fnv1(reduction, mix[word + 2]);
        mix[word / 4] = fnv1(reduction, mix[word + 3]);
    }
    for (uint32_t word = 0; word < 8; ++word)
        mix[word] = fnv1(mix[word], mix[8 + word]);

    std::memcpy(final_input.data(), seed.bytes, sizeof(seed.bytes));
    std::memcpy(final_input.data() + sizeof(seed.bytes), mix.data(), 32);
    return ethash::keccak256(final_input.data(), final_input.size());
}
}

std::unique_ptr<trmadenci_octopus_light_context>
trmadenci_create_octopus_light_context(const uint64_t block_number)
{
    const auto epoch64 = block_number / epoch_length;
    if (epoch64 > static_cast<uint64_t>(std::numeric_limits<int>::max()))
        return nullptr;
    const auto epoch = static_cast<int>(epoch64);
    const auto cache_bytes = prime_sized_bytes(cache_init, cache_growth, 64, epoch64);
    const auto dataset_bytes = prime_sized_bytes(dataset_init, dataset_growth, 256, epoch64);
    const auto cache_items = static_cast<int>(cache_bytes / sizeof(ethash::hash512));
    const auto dataset_pages = dataset_bytes / sizeof(ethash::hash2048);
    if (dataset_pages > static_cast<uint64_t>(std::numeric_limits<int>::max()))
        return nullptr;

    auto result = std::make_unique<trmadenci_octopus_light_context>();
    result->epoch = epoch;
    result->dataset_bytes = dataset_bytes;
    result->cache.resize(cache_items);
    ethash::hash256 seed{};
    for (int i = 0; i < epoch; ++i)
        seed = ethash::keccak256(seed);
    ethash::build_light_cache(result->cache.data(), cache_items, seed);
    result->ethash_context.reset(new ethash_epoch_context{
        epoch,
        cache_items,
        result->cache.data(),
        nullptr,
        static_cast<int>(dataset_pages),
        dataset_parents});
    return result;
}

int32_t trmadenci_octopus_hash_reference(
    const uint64_t block_number,
    const uint8_t header_hash[32],
    const uint64_t nonce,
    const uint64_t compressed_multi_point,
    const uint32_t points[32],
    uint8_t final_hash[32])
{
    if (header_hash == nullptr || points == nullptr || final_hash == nullptr)
        return -1;
    const auto epoch_number = block_number / epoch_length;
    if (epoch_number > static_cast<uint64_t>(std::numeric_limits<int>::max()))
        return -1;

    const std::lock_guard lock{context_mutex};
    if (!cached_context || cached_context->epoch != static_cast<int>(epoch_number))
        cached_context = trmadenci_create_octopus_light_context(block_number);
    if (!cached_context)
        return -2;

    // Nonce participates in the managed multi-point calculation. Keeping it in
    // this ABI makes accidental reuse of points for a different nonce visible.
    (void)nonce;
    const auto result = hash(*cached_context, header_hash, compressed_multi_point, points);
    std::memcpy(final_hash, result.bytes, sizeof(result.bytes));
    return 0;
}
