#pragma once

#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#  if defined(TRMADENCI_NATIVE_EXPORTS)
#    define TRMADENCI_API __declspec(dllexport)
#  else
#    define TRMADENCI_API __declspec(dllimport)
#  endif
#else
#  define TRMADENCI_API
#endif

extern "C" {

struct trmadenci_device_info
{
    int32_t index;
    char name[256];
    uint64_t total_memory_bytes;
    int32_t compute_major;
    int32_t compute_minor;
};

struct trmadenci_opencl_device_info
{
    int32_t platform_index;
    int32_t device_index;
    uint64_t total_memory_bytes;
    uint32_t compute_units;
    char platform_name[128];
    char name[256];
    char vendor[128];
    char version[128];
};

enum trmadenci_gpu_telemetry_field : uint32_t
{
    TRMADENCI_TELEMETRY_TEMPERATURE = 1u << 0,
    TRMADENCI_TELEMETRY_FAN = 1u << 1,
    TRMADENCI_TELEMETRY_POWER = 1u << 2,
    TRMADENCI_TELEMETRY_UTILIZATION = 1u << 3,
    TRMADENCI_TELEMETRY_MEMORY = 1u << 4,
    TRMADENCI_TELEMETRY_GRAPHICS_CLOCK = 1u << 5,
    TRMADENCI_TELEMETRY_MEMORY_CLOCK = 1u << 6
};

struct trmadenci_gpu_telemetry
{
    uint32_t valid_fields;
    uint32_t temperature_c;
    uint32_t fan_percent;
    uint32_t power_milliwatts;
    uint32_t gpu_utilization_percent;
    uint32_t memory_utilization_percent;
    uint64_t memory_used_bytes;
    uint64_t memory_total_bytes;
    uint32_t graphics_clock_mhz;
    uint32_t memory_clock_mhz;
};

struct trmadenci_epoch_info
{
    int32_t epoch_number;
    uint64_t light_cache_bytes;
    uint64_t full_dataset_bytes;
};

struct trmadenci_etchash_epoch_info
{
    int32_t dataset_epoch_number;
    int32_t seed_epoch_number;
    uint64_t light_cache_bytes;
    uint64_t full_dataset_bytes;
    uint8_t seed_hash[32];
};

struct trmadenci_cuda_epoch;
struct trmadenci_opencl_etchash_epoch;
struct trmadenci_randomx_context;

struct trmadenci_cuda_epoch_build_info
{
    int32_t epoch_number;
    int32_t device_index;
    uint64_t dataset_bytes;
    double build_milliseconds;
};

struct trmadenci_opencl_epoch_build_info
{
    int32_t epoch_number;
    int32_t platform_index;
    int32_t device_index;
    uint64_t dataset_bytes;
    double build_milliseconds;
};

struct trmadenci_search_result
{
    int32_t solution_found;
    uint64_t nonce;
    uint8_t mix_hash[32];
    uint8_t final_hash[32];
    uint64_t hashes_searched;
    double search_milliseconds;
};

struct trmadenci_randomx_build_info
{
    uint32_t vm_count;
    uint32_t init_threads;
    uint32_t recommended_flags;
    int32_t huge_pages_active;
    uint64_t dataset_bytes;
    double build_milliseconds;
};

TRMADENCI_API int32_t trmadenci_get_device_count();
TRMADENCI_API int32_t trmadenci_get_device_info(int32_t index, trmadenci_device_info* info);
TRMADENCI_API int32_t trmadenci_get_opencl_device_count();
TRMADENCI_API int32_t trmadenci_get_opencl_device_info(
    int32_t index,
    trmadenci_opencl_device_info* info);
TRMADENCI_API int32_t trmadenci_opencl_self_test(
    int32_t platform_index,
    int32_t device_index,
    uint32_t* checksum);
TRMADENCI_API int32_t trmadenci_validate_etchash_opencl_dag_items(
    int32_t block_number,
    int32_t platform_index,
    int32_t device_index,
    uint32_t item_count,
    uint32_t* first_mismatch);
TRMADENCI_API int32_t trmadenci_create_etchash_opencl_epoch(
    int32_t block_number,
    int32_t platform_index,
    int32_t device_index,
    struct trmadenci_opencl_etchash_epoch** epoch,
    struct trmadenci_opencl_epoch_build_info* build_info);
TRMADENCI_API void trmadenci_destroy_etchash_opencl_epoch(
    struct trmadenci_opencl_etchash_epoch* epoch);
TRMADENCI_API int32_t trmadenci_search_etchash_opencl(
    struct trmadenci_opencl_etchash_epoch* epoch,
    int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    uint64_t start_nonce,
    uint32_t nonce_count,
    struct trmadenci_search_result* result);
TRMADENCI_API int32_t trmadenci_get_gpu_telemetry(
    int32_t device_index,
    trmadenci_gpu_telemetry* telemetry);
TRMADENCI_API int32_t trmadenci_get_epoch_info(int32_t block_number, trmadenci_epoch_info* info);
TRMADENCI_API int32_t trmadenci_etchash_get_epoch_info(
    int32_t block_number,
    trmadenci_etchash_epoch_info* info);
TRMADENCI_API int32_t trmadenci_etchash_find_seed_epoch(
    const uint8_t seed_hash[32],
    int32_t* seed_epoch_number);
TRMADENCI_API int32_t trmadenci_kawpow_hash_reference(
    int32_t block_number,
    const uint8_t header_hash[32],
    uint64_t nonce,
    uint8_t mix_hash[32],
    uint8_t final_hash[32]);
TRMADENCI_API int32_t trmadenci_etchash_hash_reference(
    int32_t block_number,
    const uint8_t header_hash[32],
    uint64_t nonce,
    uint8_t mix_hash[32],
    uint8_t final_hash[32]);
TRMADENCI_API int32_t trmadenci_octopus_hash_reference(
    uint64_t block_number,
    const uint8_t header_hash[32],
    uint64_t nonce,
    uint64_t compressed_multi_point,
    const uint32_t points[32],
    uint8_t final_hash[32]);
TRMADENCI_API uint32_t trmadenci_randomx_recommended_flags();
TRMADENCI_API int32_t trmadenci_randomx_hash_light(
    const uint8_t* key,
    size_t key_size,
    const uint8_t* input,
    size_t input_size,
    uint8_t output[32]);
TRMADENCI_API int32_t trmadenci_create_randomx_context(
    const uint8_t* key,
    size_t key_size,
    uint32_t vm_count,
    uint32_t init_threads,
    int32_t use_huge_pages,
    int32_t secure_jit,
    struct trmadenci_randomx_context** context,
    struct trmadenci_randomx_build_info* build_info);
TRMADENCI_API void trmadenci_destroy_randomx_context(
    struct trmadenci_randomx_context* context);
TRMADENCI_API int32_t trmadenci_randomx_calculate_hash(
    struct trmadenci_randomx_context* context,
    uint32_t vm_index,
    const uint8_t* input,
    size_t input_size,
    uint8_t output[32]);
TRMADENCI_API int32_t trmadenci_validate_cuda_dag_items(
    int32_t block_number,
    uint32_t item_count,
    uint32_t* first_mismatch);
TRMADENCI_API int32_t trmadenci_validate_etchash_cuda_dag_items(
    int32_t block_number,
    uint32_t item_count,
    uint32_t* first_mismatch);
TRMADENCI_API int32_t trmadenci_validate_octopus_cuda_dag_items(
    uint64_t block_number,
    uint32_t item_count,
    uint32_t* first_mismatch);
TRMADENCI_API int32_t trmadenci_create_cuda_epoch(
    int32_t block_number,
    int32_t device_index,
    struct trmadenci_cuda_epoch** epoch,
    struct trmadenci_cuda_epoch_build_info* build_info);
TRMADENCI_API int32_t trmadenci_create_etchash_cuda_epoch(
    int32_t block_number,
    int32_t device_index,
    struct trmadenci_cuda_epoch** epoch,
    struct trmadenci_cuda_epoch_build_info* build_info);
TRMADENCI_API int32_t trmadenci_create_octopus_cuda_epoch(
    uint64_t block_number,
    int32_t device_index,
    struct trmadenci_cuda_epoch** epoch,
    struct trmadenci_cuda_epoch_build_info* build_info);
TRMADENCI_API void trmadenci_destroy_cuda_epoch(struct trmadenci_cuda_epoch* epoch);
TRMADENCI_API int32_t trmadenci_search_cuda(
    struct trmadenci_cuda_epoch* epoch,
    int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    uint64_t start_nonce,
    uint32_t nonce_count,
    struct trmadenci_search_result* result);
TRMADENCI_API int32_t trmadenci_search_etchash_cuda(
    struct trmadenci_cuda_epoch* epoch,
    int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    uint64_t start_nonce,
    uint32_t nonce_count,
    struct trmadenci_search_result* result);
TRMADENCI_API int32_t trmadenci_search_octopus_cuda(
    struct trmadenci_cuda_epoch* epoch,
    uint64_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    uint64_t start_nonce,
    uint32_t nonce_count,
    struct trmadenci_search_result* result);
TRMADENCI_API const char* trmadenci_get_last_error();

}
