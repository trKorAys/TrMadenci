#pragma once

#include <cstdint>

struct trmadenci_cuda_epoch
{
    int32_t device_index;
    int32_t epoch_number;
    int32_t algorithm_kind;
    uint32_t full_dataset_items;
    uint64_t dataset_bytes;
    uint32_t* dataset;
    int32_t jit_program_seed;
    void* jit_module;
    void* jit_seed_function;
    void* jit_mix_function;
    void* jit_final_function;
    uint32_t search_capacity;
    uint32_t* search_header;
    uint8_t* search_target;
    uint32_t* search_initial;
    uint32_t* search_reduced;
    void* search_result;
};
