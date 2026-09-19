#include "trmadenci_native.h"

#include <randomx.h>

#include <cstddef>
#include <cstdint>
#include <algorithm>
#include <chrono>
#include <new>
#include <thread>
#include <vector>

struct trmadenci_randomx_context
{
    randomx_dataset* dataset = nullptr;
    std::vector<randomx_vm*> vms;
};

namespace
{
void destroy_context(trmadenci_randomx_context* context)
{
    if (context == nullptr)
        return;
    for (auto* vm : context->vms)
    {
        if (vm != nullptr)
            randomx_destroy_vm(vm);
    }
    if (context->dataset != nullptr)
        randomx_release_dataset(context->dataset);
    delete context;
}

randomx_flags add_flag(const randomx_flags flags, const randomx_flags flag)
{
    return static_cast<randomx_flags>(static_cast<uint32_t>(flags) | static_cast<uint32_t>(flag));
}

randomx_flags remove_flag(const randomx_flags flags, const randomx_flags flag)
{
    return static_cast<randomx_flags>(static_cast<uint32_t>(flags) & ~static_cast<uint32_t>(flag));
}
}

uint32_t trmadenci_randomx_recommended_flags()
{
    return static_cast<uint32_t>(randomx_get_flags());
}

int32_t trmadenci_randomx_hash_light(
    const uint8_t* key,
    const size_t key_size,
    const uint8_t* input,
    const size_t input_size,
    uint8_t output[32])
{
    if (key == nullptr || key_size == 0 || input == nullptr || input_size == 0 || output == nullptr)
        return -1;

    auto* cache = randomx_alloc_cache(RANDOMX_FLAG_DEFAULT);
    if (cache == nullptr)
        return -2;

    randomx_init_cache(cache, key, key_size);
    auto* vm = randomx_create_vm(RANDOMX_FLAG_DEFAULT, cache, nullptr);
    if (vm == nullptr)
    {
        randomx_release_cache(cache);
        return -3;
    }

    randomx_calculate_hash(vm, input, input_size, output);
    randomx_destroy_vm(vm);
    randomx_release_cache(cache);
    return 0;
}

int32_t trmadenci_create_randomx_context(
    const uint8_t* key,
    const size_t key_size,
    const uint32_t vm_count,
    const uint32_t init_threads,
    const int32_t use_huge_pages,
    const int32_t secure_jit,
    trmadenci_randomx_context** context,
    trmadenci_randomx_build_info* build_info)
{
    if (key == nullptr || key_size == 0 || vm_count == 0 || init_threads == 0 ||
        context == nullptr || build_info == nullptr)
        return -1;

    *context = nullptr;
    *build_info = {};
    randomx_cache* cache = nullptr;
    auto* created = new (std::nothrow) trmadenci_randomx_context{};
    if (created == nullptr)
        return -2;

    try
    {
        const auto started = std::chrono::steady_clock::now();
        const auto recommended = randomx_get_flags();
        constexpr auto cache_mask = static_cast<randomx_flags>(
            RANDOMX_FLAG_JIT | RANDOMX_FLAG_ARGON2);
        auto cache_flags = static_cast<randomx_flags>(recommended & cache_mask);
        auto dataset_flags = RANDOMX_FLAG_DEFAULT;
        auto huge_pages_active = use_huge_pages != 0;
        if (huge_pages_active)
        {
            cache_flags = add_flag(cache_flags, RANDOMX_FLAG_LARGE_PAGES);
            dataset_flags = add_flag(dataset_flags, RANDOMX_FLAG_LARGE_PAGES);
        }

        cache = randomx_alloc_cache(cache_flags);
        created->dataset = randomx_alloc_dataset(dataset_flags);
        if ((cache == nullptr || created->dataset == nullptr) && huge_pages_active)
        {
            if (cache != nullptr)
                randomx_release_cache(cache);
            if (created->dataset != nullptr)
                randomx_release_dataset(created->dataset);
            cache = nullptr;
            created->dataset = nullptr;
            huge_pages_active = false;
            cache_flags = remove_flag(cache_flags, RANDOMX_FLAG_LARGE_PAGES);
            cache = randomx_alloc_cache(cache_flags);
            created->dataset = randomx_alloc_dataset(RANDOMX_FLAG_DEFAULT);
        }
        if (cache == nullptr || created->dataset == nullptr)
        {
            if (cache != nullptr)
                randomx_release_cache(cache);
            destroy_context(created);
            return -3;
        }

        randomx_init_cache(cache, key, key_size);
        const auto item_count = randomx_dataset_item_count();
        const auto worker_count = std::min<uint32_t>(init_threads, item_count);
        std::vector<std::thread> workers;
        workers.reserve(worker_count);
        const auto base_count = item_count / worker_count;
        const auto remainder = item_count % worker_count;
        unsigned long first_item = 0;
        try
        {
            for (uint32_t index = 0; index < worker_count; ++index)
            {
                const auto count = base_count + (index < remainder ? 1UL : 0UL);
                workers.emplace_back(
                    randomx_init_dataset,
                    created->dataset,
                    cache,
                    first_item,
                    count);
                first_item += count;
            }
        }
        catch (...)
        {
            for (auto& worker : workers)
            {
                if (worker.joinable())
                    worker.join();
            }
            throw;
        }
        for (auto& worker : workers)
        {
            if (worker.joinable())
                worker.join();
        }
        randomx_release_cache(cache);
        cache = nullptr;

        constexpr auto vm_mask = static_cast<randomx_flags>(RANDOMX_FLAG_HARD_AES | RANDOMX_FLAG_JIT);
        auto vm_flags = add_flag(
            static_cast<randomx_flags>(recommended & vm_mask),
            RANDOMX_FLAG_FULL_MEM);
        if (secure_jit != 0 && (vm_flags & RANDOMX_FLAG_JIT) != 0)
            vm_flags = add_flag(vm_flags, RANDOMX_FLAG_SECURE);
        if (huge_pages_active)
            vm_flags = add_flag(vm_flags, RANDOMX_FLAG_LARGE_PAGES);

        created->vms.reserve(vm_count);
        for (uint32_t index = 0; index < vm_count; ++index)
        {
            auto* vm = randomx_create_vm(vm_flags, nullptr, created->dataset);
            if (vm == nullptr && huge_pages_active)
            {
                vm_flags = remove_flag(vm_flags, RANDOMX_FLAG_LARGE_PAGES);
                huge_pages_active = false;
                vm = randomx_create_vm(vm_flags, nullptr, created->dataset);
            }
            if (vm == nullptr)
            {
                destroy_context(created);
                return -4;
            }
            created->vms.push_back(vm);
        }

        const auto elapsed = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - started);
        build_info->vm_count = vm_count;
        build_info->init_threads = worker_count;
        build_info->recommended_flags = static_cast<uint32_t>(recommended);
        build_info->huge_pages_active = huge_pages_active ? 1 : 0;
        build_info->dataset_bytes = static_cast<uint64_t>(item_count) * RANDOMX_DATASET_ITEM_SIZE;
        build_info->build_milliseconds = elapsed.count();
        *context = created;
        return 0;
    }
    catch (...)
    {
        if (cache != nullptr)
            randomx_release_cache(cache);
        destroy_context(created);
        return -5;
    }
}

void trmadenci_destroy_randomx_context(trmadenci_randomx_context* context)
{
    destroy_context(context);
}

int32_t trmadenci_randomx_calculate_hash(
    trmadenci_randomx_context* context,
    const uint32_t vm_index,
    const uint8_t* input,
    const size_t input_size,
    uint8_t output[32])
{
    if (context == nullptr || vm_index >= context->vms.size() ||
        input == nullptr || input_size == 0 || output == nullptr)
        return -1;
    randomx_calculate_hash(context->vms[vm_index], input, input_size, output);
    return 0;
}
