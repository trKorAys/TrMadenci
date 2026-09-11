#pragma once

#include "ethash-internal.hpp"

#include <cstdint>
#include <memory>
#include <vector>

struct trmadenci_octopus_light_context
{
    int epoch;
    uint64_t dataset_bytes;
    std::vector<ethash::hash512> cache;
    std::unique_ptr<ethash_epoch_context> ethash_context;
};

std::unique_ptr<trmadenci_octopus_light_context>
trmadenci_create_octopus_light_context(uint64_t block_number);
