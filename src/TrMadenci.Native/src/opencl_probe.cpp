#include "trmadenci_native.h"
#include "ethash-internal.hpp"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <string>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace
{
using cl_int = int32_t;
using cl_uint = uint32_t;
using cl_ulong = uint64_t;
using cl_device_type = cl_ulong;
using cl_platform_info = cl_uint;
using cl_device_info = cl_uint;
using cl_platform_id = void*;
using cl_device_id = void*;
using cl_context = void*;
using cl_command_queue = void*;
using cl_program = void*;
using cl_kernel = void*;
using cl_mem = void*;
using cl_event = void*;
using cl_mem_flags = cl_ulong;
using cl_bool = cl_uint;

constexpr cl_int success = 0;
constexpr cl_int device_not_found = -1;
constexpr cl_int platform_not_found = -1001;
constexpr cl_device_type device_type_gpu = 1ULL << 2;
constexpr cl_platform_info platform_name = 0x0902;
constexpr cl_device_info device_name = 0x102b;
constexpr cl_device_info device_vendor = 0x102c;
constexpr cl_device_info device_version = 0x102f;
constexpr cl_device_info device_max_compute_units = 0x1002;
constexpr cl_device_info device_global_mem_size = 0x101f;
constexpr cl_mem_flags mem_write_only = 1ULL << 1;
constexpr cl_mem_flags mem_read_only = 1ULL << 2;
constexpr cl_mem_flags mem_read_write = 1ULL << 0;
constexpr cl_bool true_value = 1;

using get_platform_ids_fn = cl_int(__stdcall*)(cl_uint, cl_platform_id*, cl_uint*);
using get_platform_info_fn = cl_int(__stdcall*)(cl_platform_id, cl_platform_info, size_t, void*, size_t*);
using get_device_ids_fn = cl_int(__stdcall*)(cl_platform_id, cl_device_type, cl_uint, cl_device_id*, cl_uint*);
using get_device_info_fn = cl_int(__stdcall*)(cl_device_id, cl_device_info, size_t, void*, size_t*);
using create_context_fn = cl_context(__stdcall*)(
    const intptr_t*, cl_uint, const cl_device_id*, void*, void*, cl_int*);
using create_command_queue_fn = cl_command_queue(__stdcall*)(cl_context, cl_device_id, cl_ulong, cl_int*);
using create_program_with_source_fn = cl_program(__stdcall*)(
    cl_context, cl_uint, const char**, const size_t*, cl_int*);
using build_program_fn = cl_int(__stdcall*)(cl_program, cl_uint, const cl_device_id*, const char*, void*, void*);
using create_kernel_fn = cl_kernel(__stdcall*)(cl_program, const char*, cl_int*);
using create_buffer_fn = cl_mem(__stdcall*)(cl_context, cl_mem_flags, size_t, void*, cl_int*);
using set_kernel_arg_fn = cl_int(__stdcall*)(cl_kernel, cl_uint, size_t, const void*);
using enqueue_write_buffer_fn = cl_int(__stdcall*)(
    cl_command_queue, cl_mem, cl_bool, size_t, size_t, const void*, cl_uint, const cl_event*, cl_event*);
using enqueue_ndrange_kernel_fn = cl_int(__stdcall*)(
    cl_command_queue, cl_kernel, cl_uint, const size_t*, const size_t*, const size_t*,
    cl_uint, const cl_event*, cl_event*);
using enqueue_read_buffer_fn = cl_int(__stdcall*)(
    cl_command_queue, cl_mem, cl_bool, size_t, size_t, void*, cl_uint, const cl_event*, cl_event*);
using release_mem_object_fn = cl_int(__stdcall*)(cl_mem);
using release_kernel_fn = cl_int(__stdcall*)(cl_kernel);
using release_program_fn = cl_int(__stdcall*)(cl_program);
using release_command_queue_fn = cl_int(__stdcall*)(cl_command_queue);
using release_context_fn = cl_int(__stdcall*)(cl_context);

struct opencl_api
{
    HMODULE library{};
    get_platform_ids_fn get_platform_ids{};
    get_platform_info_fn get_platform_info{};
    get_device_ids_fn get_device_ids{};
    get_device_info_fn get_device_info{};
    create_context_fn create_context{};
    create_command_queue_fn create_command_queue{};
    create_program_with_source_fn create_program_with_source{};
    build_program_fn build_program{};
    create_kernel_fn create_kernel{};
    create_buffer_fn create_buffer{};
    set_kernel_arg_fn set_kernel_arg{};
    enqueue_write_buffer_fn enqueue_write_buffer{};
    enqueue_ndrange_kernel_fn enqueue_ndrange_kernel{};
    enqueue_read_buffer_fn enqueue_read_buffer{};
    release_mem_object_fn release_mem_object{};
    release_kernel_fn release_kernel{};
    release_program_fn release_program{};
    release_command_queue_fn release_command_queue{};
    release_context_fn release_context{};

    opencl_api()
    {
        library = LoadLibraryW(L"OpenCL.dll");
        if (library == nullptr)
            return;
        get_platform_ids = reinterpret_cast<get_platform_ids_fn>(GetProcAddress(library, "clGetPlatformIDs"));
        get_platform_info = reinterpret_cast<get_platform_info_fn>(GetProcAddress(library, "clGetPlatformInfo"));
        get_device_ids = reinterpret_cast<get_device_ids_fn>(GetProcAddress(library, "clGetDeviceIDs"));
        get_device_info = reinterpret_cast<get_device_info_fn>(GetProcAddress(library, "clGetDeviceInfo"));
        create_context = reinterpret_cast<create_context_fn>(GetProcAddress(library, "clCreateContext"));
        create_command_queue = reinterpret_cast<create_command_queue_fn>(GetProcAddress(library, "clCreateCommandQueue"));
        create_program_with_source = reinterpret_cast<create_program_with_source_fn>(
            GetProcAddress(library, "clCreateProgramWithSource"));
        build_program = reinterpret_cast<build_program_fn>(GetProcAddress(library, "clBuildProgram"));
        create_kernel = reinterpret_cast<create_kernel_fn>(GetProcAddress(library, "clCreateKernel"));
        create_buffer = reinterpret_cast<create_buffer_fn>(GetProcAddress(library, "clCreateBuffer"));
        set_kernel_arg = reinterpret_cast<set_kernel_arg_fn>(GetProcAddress(library, "clSetKernelArg"));
        enqueue_write_buffer = reinterpret_cast<enqueue_write_buffer_fn>(
            GetProcAddress(library, "clEnqueueWriteBuffer"));
        enqueue_ndrange_kernel = reinterpret_cast<enqueue_ndrange_kernel_fn>(
            GetProcAddress(library, "clEnqueueNDRangeKernel"));
        enqueue_read_buffer = reinterpret_cast<enqueue_read_buffer_fn>(
            GetProcAddress(library, "clEnqueueReadBuffer"));
        release_mem_object = reinterpret_cast<release_mem_object_fn>(GetProcAddress(library, "clReleaseMemObject"));
        release_kernel = reinterpret_cast<release_kernel_fn>(GetProcAddress(library, "clReleaseKernel"));
        release_program = reinterpret_cast<release_program_fn>(GetProcAddress(library, "clReleaseProgram"));
        release_command_queue = reinterpret_cast<release_command_queue_fn>(
            GetProcAddress(library, "clReleaseCommandQueue"));
        release_context = reinterpret_cast<release_context_fn>(GetProcAddress(library, "clReleaseContext"));
    }

    ~opencl_api()
    {
        if (library != nullptr)
            FreeLibrary(library);
    }

    bool available() const noexcept
    {
        return library != nullptr && get_platform_ids != nullptr && get_platform_info != nullptr &&
            get_device_ids != nullptr && get_device_info != nullptr;
    }

    bool runtime_available() const noexcept
    {
        return available() && create_context != nullptr && create_command_queue != nullptr &&
            create_program_with_source != nullptr && build_program != nullptr && create_kernel != nullptr &&
            create_buffer != nullptr && set_kernel_arg != nullptr && enqueue_write_buffer != nullptr &&
            enqueue_ndrange_kernel != nullptr && enqueue_read_buffer != nullptr &&
            release_mem_object != nullptr && release_kernel != nullptr && release_program != nullptr &&
            release_command_queue != nullptr && release_context != nullptr;
    }
};

struct discovered_device
{
    int32_t platform_index;
    int32_t device_index;
    cl_platform_id platform;
    cl_device_id device;
};

opencl_api& api()
{
    static opencl_api instance;
    return instance;
}

std::vector<discovered_device> discover()
{
    auto& opencl = api();
    if (!opencl.available())
        return {};
    cl_uint platform_count = 0;
    const auto platform_status = opencl.get_platform_ids(0, nullptr, &platform_count);
    if ((platform_status != success && platform_status != platform_not_found) || platform_count == 0)
        return {};
    std::vector<cl_platform_id> platforms(platform_count);
    if (opencl.get_platform_ids(platform_count, platforms.data(), nullptr) != success)
        return {};

    std::vector<discovered_device> result;
    for (cl_uint platform_index = 0; platform_index < platform_count; ++platform_index)
    {
        cl_uint device_count = 0;
        const auto device_status = opencl.get_device_ids(
            platforms[platform_index], device_type_gpu, 0, nullptr, &device_count);
        if (device_status == device_not_found || device_count == 0)
            continue;
        if (device_status != success)
            return {};
        std::vector<cl_device_id> devices(device_count);
        if (opencl.get_device_ids(
            platforms[platform_index], device_type_gpu, device_count, devices.data(), nullptr) != success)
            return {};
        for (cl_uint device_index = 0; device_index < device_count; ++device_index)
            result.push_back({
                static_cast<int32_t>(platform_index),
                static_cast<int32_t>(device_index),
                platforms[platform_index],
                devices[device_index]});
    }
    return result;
}

const std::vector<discovered_device>& discovered_devices()
{
    static const auto devices = discover();
    return devices;
}

const discovered_device* find_device(const int32_t platform_index, const int32_t device_index)
{
    const auto& devices = discovered_devices();
    const auto selected = std::find_if(devices.begin(), devices.end(), [=](const discovered_device& device)
    {
        return device.platform_index == platform_index && device.device_index == device_index;
    });
    return selected == devices.end() ? nullptr : &*selected;
}

constexpr char etchash_dag_source[] = R"CLC(
inline ulong rol64(ulong value, uint shift) {
    return shift == 0 ? value : (value << shift) | (value >> (64 - shift));
}

inline void keccak_f1600(ulong state[25]) {
    const ulong rc[24] = {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    };
    const uint rho[25] = {
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43,
        25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14
    };
    for (uint round = 0; round < 24; ++round) {
        ulong column[5];
        ulong delta[5];
        ulong rotated[25];
        for (uint x = 0; x < 5; ++x)
            column[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
        for (uint x = 0; x < 5; ++x)
            delta[x] = column[(x + 4) % 5] ^ rol64(column[(x + 1) % 5], 1);
        for (uint y = 0; y < 5; ++y)
            for (uint x = 0; x < 5; ++x)
                state[x + 5 * y] ^= delta[x];
        for (uint y = 0; y < 5; ++y)
            for (uint x = 0; x < 5; ++x)
                rotated[y + 5 * ((2 * x + 3 * y) % 5)] = rol64(state[x + 5 * y], rho[x + 5 * y]);
        for (uint y = 0; y < 5; ++y)
            for (uint x = 0; x < 5; ++x)
                state[x + 5 * y] = rotated[x + 5 * y] ^
                    ((~rotated[(x + 1) % 5 + 5 * y]) & rotated[(x + 2) % 5 + 5 * y]);
        state[0] ^= rc[round];
    }
}

inline void keccak512_64(uint words[16]) {
    ulong state[25];
    for (uint i = 0; i < 25; ++i) state[i] = 0;
    for (uint i = 0; i < 8; ++i)
        state[i] = (ulong)words[i * 2] | ((ulong)words[i * 2 + 1] << 32);
    state[8] = 0x8000000000000001UL;
    keccak_f1600(state);
    for (uint i = 0; i < 8; ++i) {
        words[i * 2] = (uint)state[i];
        words[i * 2 + 1] = (uint)(state[i] >> 32);
    }
}

__kernel void generate_etchash_nodes(
    __global const uint* light,
    uint light_items,
    __global uint* output,
    uint start_node,
    uint node_count) {
    const uint relative_node = (uint)get_global_id(0);
    if (relative_node >= node_count) return;
    const uint node = start_node + relative_node;
    uint mix[16];
    const uint source = (node % light_items) * 16;
    for (uint word = 0; word < 16; ++word) mix[word] = light[source + word];
    mix[0] ^= node;
    keccak512_64(mix);
    for (uint round = 0; round < 256; ++round) {
        const uint parent = (((node ^ round) * 0x01000193U) ^ mix[round % 16]) % light_items;
        const uint parent_offset = parent * 16;
        for (uint word = 0; word < 16; ++word)
            mix[word] = (mix[word] * 0x01000193U) ^ light[parent_offset + word];
    }
    keccak512_64(mix);
    const uint destination = node * 16;
    for (uint word = 0; word < 16; ++word) output[destination + word] = mix[word];
}

inline int etchash_meets_target(const uint hash[8], __global const uchar* target) {
    for (uint byte_index = 0; byte_index < 32; ++byte_index) {
        const uchar hash_byte = (uchar)(hash[byte_index / 4] >> ((byte_index % 4) * 8));
        if (hash_byte < target[byte_index]) return 1;
        if (hash_byte > target[byte_index]) return 0;
    }
    return 1;
}

__kernel void etchash_seed(
    __global const uint* header,
    ulong start_nonce,
    uint nonce_count,
    __global uint* initial) {
    const uint index = (uint)get_global_id(0);
    if (index >= nonce_count) return;
    ulong state[25];
    for (uint word = 0; word < 25; ++word) state[word] = 0;
    for (uint word = 0; word < 4; ++word)
        state[word] = (ulong)header[word * 2] | ((ulong)header[word * 2 + 1] << 32);
    state[4] = start_nonce + index;
    state[5] = 0x0000000000000001UL;
    state[8] = 0x8000000000000000UL;
    keccak_f1600(state);
    const ulong offset = (ulong)index * 16;
    for (uint word = 0; word < 16; ++word)
        initial[offset + word] = (uint)(state[word / 2] >> ((word & 1) * 32));
}

__kernel void etchash_mix(
    __global const uint* dataset,
    uint full_dataset_items,
    __global const uint* initial,
    uint nonce_count,
    __global uint* reduced_output) {
    const uint index = (uint)get_global_id(0);
    if (index >= nonce_count) return;
    const ulong seed_offset = (ulong)index * 16;
    uint mix[32];
    for (uint word = 0; word < 16; ++word) {
        mix[word] = initial[seed_offset + word];
        mix[word + 16] = mix[word];
    }
    const uint seed_head = mix[0];
    for (uint access = 0; access < 64; ++access) {
        const uint item = (((access ^ seed_head) * 0x01000193U) ^ mix[access & 31U]) %
            full_dataset_items;
        const ulong data_offset = (ulong)item * 32;
        for (uint word = 0; word < 32; ++word)
            mix[word] = (mix[word] * 0x01000193U) ^ dataset[data_offset + word];
    }
    const ulong output_offset = (ulong)index * 8;
    for (uint word = 0; word < 8; ++word) {
        const uint offset = word * 4;
        uint value = (mix[offset] * 0x01000193U) ^ mix[offset + 1];
        value = (value * 0x01000193U) ^ mix[offset + 2];
        reduced_output[output_offset + word] = (value * 0x01000193U) ^ mix[offset + 3];
    }
}

__kernel void etchash_final(
    __global const uint* initial,
    __global const uint* reduced,
    __global const uchar* target,
    ulong start_nonce,
    uint nonce_count,
    volatile __global uint* solution_found,
    __global ulong* solution_nonce,
    __global uint* solution_mix,
    __global uint* solution_hash) {
    const uint index = (uint)get_global_id(0);
    if (index >= nonce_count) return;
    const ulong seed_offset = (ulong)index * 16;
    const ulong mix_offset = (ulong)index * 8;
    ulong state[25];
    for (uint word = 0; word < 25; ++word) state[word] = 0;
    for (uint word = 0; word < 8; ++word)
        state[word] = (ulong)initial[seed_offset + word * 2] |
            ((ulong)initial[seed_offset + word * 2 + 1] << 32);
    for (uint word = 0; word < 4; ++word)
        state[word + 8] = (ulong)reduced[mix_offset + word * 2] |
            ((ulong)reduced[mix_offset + word * 2 + 1] << 32);
    state[12] = 0x0000000000000001UL;
    state[16] = 0x8000000000000000UL;
    keccak_f1600(state);
    uint final_hash[8];
    for (uint word = 0; word < 8; ++word)
        final_hash[word] = (uint)(state[word / 2] >> ((word & 1) * 32));
    if (!etchash_meets_target(final_hash, target) || atomic_cmpxchg(solution_found, 0U, 1U) != 0U)
        return;
    *solution_nonce = start_nonce + index;
    for (uint word = 0; word < 8; ++word) {
        solution_mix[word] = reduced[mix_offset + word];
        solution_hash[word] = final_hash[word];
    }
}
)CLC";

template <size_t Size>
void read_platform_string(cl_platform_id platform, cl_platform_info property, char (&output)[Size])
{
    output[0] = '\0';
    api().get_platform_info(platform, property, Size, output, nullptr);
    output[Size - 1] = '\0';
}

template <size_t Size>
void read_device_string(cl_device_id device, cl_device_info property, char (&output)[Size])
{
    output[0] = '\0';
    api().get_device_info(device, property, Size, output, nullptr);
    output[Size - 1] = '\0';
}
}

struct trmadenci_opencl_etchash_epoch
{
    int32_t platform_index{};
    int32_t device_index{};
    int32_t epoch_number{};
    uint32_t full_dataset_items{};
    uint64_t dataset_bytes{};
    cl_context context{};
    cl_command_queue queue{};
    cl_program program{};
    cl_mem dataset{};
    cl_kernel seed_kernel{};
    cl_kernel mix_kernel{};
    cl_kernel final_kernel{};
    uint32_t search_capacity{};
    cl_mem search_header{};
    cl_mem search_target{};
    cl_mem search_initial{};
    cl_mem search_reduced{};
    cl_mem search_found{};
    cl_mem search_nonce{};
    cl_mem search_mix{};
    cl_mem search_hash{};
};

cl_int ensure_opencl_search_buffers(
    trmadenci_opencl_etchash_epoch* epoch,
    const uint32_t nonce_count)
{
    auto& opencl = api();
    auto status = success;
    if (epoch->search_header == nullptr)
        epoch->search_header = opencl.create_buffer(epoch->context, mem_read_only, 32, nullptr, &status);
    if (status == success && epoch->search_target == nullptr)
        epoch->search_target = opencl.create_buffer(epoch->context, mem_read_only, 32, nullptr, &status);
    if (status == success && epoch->search_found == nullptr)
        epoch->search_found = opencl.create_buffer(epoch->context, mem_read_write, sizeof(cl_uint), nullptr, &status);
    if (status == success && epoch->search_nonce == nullptr)
        epoch->search_nonce = opencl.create_buffer(epoch->context, mem_read_write, sizeof(cl_ulong), nullptr, &status);
    if (status == success && epoch->search_mix == nullptr)
        epoch->search_mix = opencl.create_buffer(epoch->context, mem_read_write, 32, nullptr, &status);
    if (status == success && epoch->search_hash == nullptr)
        epoch->search_hash = opencl.create_buffer(epoch->context, mem_read_write, 32, nullptr, &status);
    if (status != success || epoch->search_capacity >= nonce_count)
        return status;
    if (epoch->search_reduced != nullptr) opencl.release_mem_object(epoch->search_reduced);
    if (epoch->search_initial != nullptr) opencl.release_mem_object(epoch->search_initial);
    epoch->search_reduced = nullptr;
    epoch->search_initial = nullptr;
    epoch->search_capacity = 0;
    epoch->search_initial = opencl.create_buffer(
        epoch->context, mem_read_write,
        static_cast<size_t>(nonce_count) * 16 * sizeof(uint32_t), nullptr, &status);
    if (status == success)
        epoch->search_reduced = opencl.create_buffer(
            epoch->context, mem_read_write,
            static_cast<size_t>(nonce_count) * 8 * sizeof(uint32_t), nullptr, &status);
    if (status == success)
        epoch->search_capacity = nonce_count;
    return status;
}

int32_t trmadenci_get_opencl_device_count()
{
    return static_cast<int32_t>(discovered_devices().size());
}

int32_t trmadenci_get_opencl_device_info(
    const int32_t index,
    trmadenci_opencl_device_info* info)
{
    if (index < 0 || info == nullptr)
        return -1;
    const auto& devices = discovered_devices();
    if (static_cast<size_t>(index) >= devices.size())
        return -1;
    const auto& selected = devices[static_cast<size_t>(index)];
    *info = {};
    info->platform_index = selected.platform_index;
    info->device_index = selected.device_index;
    read_platform_string(selected.platform, platform_name, info->platform_name);
    read_device_string(selected.device, device_name, info->name);
    read_device_string(selected.device, device_vendor, info->vendor);
    read_device_string(selected.device, device_version, info->version);
    api().get_device_info(
        selected.device, device_global_mem_size, sizeof(info->total_memory_bytes),
        &info->total_memory_bytes, nullptr);
    api().get_device_info(
        selected.device, device_max_compute_units, sizeof(info->compute_units),
        &info->compute_units, nullptr);
    return 0;
}

int32_t trmadenci_opencl_self_test(
    const int32_t platform_index,
    const int32_t device_index,
    uint32_t* checksum)
{
    if (checksum == nullptr || !api().runtime_available())
        return -1;
    const auto& devices = discovered_devices();
    const auto selected = std::find_if(devices.begin(), devices.end(), [=](const discovered_device& device)
    {
        return device.platform_index == platform_index && device.device_index == device_index;
    });
    if (selected == devices.end())
        return -1;

    constexpr size_t item_count = 256;
    constexpr char source[] =
        "__kernel void trmadenci_self_test(__global const uint* input, __global uint* output) {"
        "size_t i = get_global_id(0);"
        "output[i] = (input[i] * 1664525u + 1013904223u) ^ 0xa5a5a5a5u;"
        "}";
    std::vector<uint32_t> input(item_count);
    std::vector<uint32_t> output(item_count);
    for (size_t index = 0; index < item_count; ++index)
        input[index] = static_cast<uint32_t>(index) ^ 0x5a5a5a5au;

    auto& opencl = api();
    cl_int status = success;
    auto context = opencl.create_context(nullptr, 1, &selected->device, nullptr, nullptr, &status);
    cl_command_queue queue = nullptr;
    cl_program program = nullptr;
    cl_kernel kernel = nullptr;
    cl_mem input_buffer = nullptr;
    cl_mem output_buffer = nullptr;
    const auto cleanup = [&]()
    {
        if (output_buffer != nullptr) opencl.release_mem_object(output_buffer);
        if (input_buffer != nullptr) opencl.release_mem_object(input_buffer);
        if (kernel != nullptr) opencl.release_kernel(kernel);
        if (program != nullptr) opencl.release_program(program);
        if (queue != nullptr) opencl.release_command_queue(queue);
        if (context != nullptr) opencl.release_context(context);
    };
    if (status != success || context == nullptr)
    {
        cleanup();
        return -2;
    }
    queue = opencl.create_command_queue(context, selected->device, 0, &status);
    const char* source_pointer = source;
    if (status == success)
        program = opencl.create_program_with_source(context, 1, &source_pointer, nullptr, &status);
    if (status == success)
        status = opencl.build_program(program, 1, &selected->device, nullptr, nullptr, nullptr);
    if (status == success)
        kernel = opencl.create_kernel(program, "trmadenci_self_test", &status);
    if (status == success)
        input_buffer = opencl.create_buffer(context, mem_read_only, input.size() * sizeof(uint32_t), nullptr, &status);
    if (status == success)
        output_buffer = opencl.create_buffer(context, mem_write_only, output.size() * sizeof(uint32_t), nullptr, &status);
    if (status == success)
        status = opencl.enqueue_write_buffer(
            queue, input_buffer, true_value, 0, input.size() * sizeof(uint32_t),
            input.data(), 0, nullptr, nullptr);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 0, sizeof(input_buffer), &input_buffer);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 1, sizeof(output_buffer), &output_buffer);
    if (status == success)
        status = opencl.enqueue_ndrange_kernel(
            queue, kernel, 1, nullptr, &item_count, nullptr, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_read_buffer(
            queue, output_buffer, true_value, 0, output.size() * sizeof(uint32_t),
            output.data(), 0, nullptr, nullptr);

    uint32_t value = 0x811c9dc5u;
    if (status == success)
    {
        for (size_t index = 0; index < item_count; ++index)
        {
            const auto expected = (input[index] * 1664525u + 1013904223u) ^ 0xa5a5a5a5u;
            if (output[index] != expected)
            {
                cleanup();
                return -3;
            }
            value = (value ^ output[index]) * 0x01000193u;
        }
    }
    cleanup();
    if (status != success)
        return -10000 + status;
    *checksum = value;
    return 0;
}

int32_t trmadenci_validate_etchash_opencl_dag_items(
    const int32_t block_number,
    const int32_t platform_index,
    const int32_t device_index,
    const uint32_t item_count,
    uint32_t* first_mismatch)
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;
    if (block_number < 0 || item_count == 0 || item_count > 1024 ||
        first_mismatch == nullptr || !api().runtime_available())
        return -1;
    *first_mismatch = UINT32_MAX;
    const auto* selected = find_device(platform_index, device_index);
    if (selected == nullptr)
        return -1;

    const auto epoch_length = block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    ethash::epoch_context_ptr cpu_context{
        ethash_create_epoch_context_configured(dataset_epoch, seed_epoch, 256),
        ethash_destroy_epoch_context};
    if (!cpu_context)
        return -2;

    const auto light_bytes = ethash::get_light_cache_size(cpu_context->light_cache_num_items);
    const auto output_bytes = static_cast<size_t>(item_count) * sizeof(ethash::hash1024);
    const auto node_count = item_count * 2;
    std::vector<ethash::hash1024> output(item_count);
    auto& opencl = api();
    cl_int status = success;
    auto context = opencl.create_context(nullptr, 1, &selected->device, nullptr, nullptr, &status);
    cl_command_queue queue = nullptr;
    cl_program program = nullptr;
    cl_kernel kernel = nullptr;
    cl_mem light_buffer = nullptr;
    cl_mem output_buffer = nullptr;
    const auto cleanup = [&]()
    {
        if (output_buffer != nullptr) opencl.release_mem_object(output_buffer);
        if (light_buffer != nullptr) opencl.release_mem_object(light_buffer);
        if (kernel != nullptr) opencl.release_kernel(kernel);
        if (program != nullptr) opencl.release_program(program);
        if (queue != nullptr) opencl.release_command_queue(queue);
        if (context != nullptr) opencl.release_context(context);
    };
    if (status != success || context == nullptr)
    {
        cleanup();
        return -3;
    }
    queue = opencl.create_command_queue(context, selected->device, 0, &status);
    const char* source_pointer = etchash_dag_source;
    if (status == success)
        program = opencl.create_program_with_source(context, 1, &source_pointer, nullptr, &status);
    if (status == success)
        status = opencl.build_program(program, 1, &selected->device, nullptr, nullptr, nullptr);
    if (status == success)
        kernel = opencl.create_kernel(program, "generate_etchash_nodes", &status);
    if (status == success)
        light_buffer = opencl.create_buffer(context, mem_read_only, light_bytes, nullptr, &status);
    if (status == success)
        output_buffer = opencl.create_buffer(context, mem_write_only, output_bytes, nullptr, &status);
    if (status == success)
        status = opencl.enqueue_write_buffer(
            queue, light_buffer, true_value, 0, light_bytes,
            cpu_context->light_cache, 0, nullptr, nullptr);
    const auto light_items = static_cast<uint32_t>(cpu_context->light_cache_num_items);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 0, sizeof(light_buffer), &light_buffer);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 1, sizeof(light_items), &light_items);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 2, sizeof(output_buffer), &output_buffer);
    if (status == success)
    {
        constexpr uint32_t start_node = 0;
        status = opencl.set_kernel_arg(kernel, 3, sizeof(start_node), &start_node);
    }
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 4, sizeof(node_count), &node_count);
    const auto global_size = static_cast<size_t>(node_count);
    if (status == success)
        status = opencl.enqueue_ndrange_kernel(
            queue, kernel, 1, nullptr, &global_size, nullptr, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_read_buffer(
            queue, output_buffer, true_value, 0, output_bytes,
            output.data(), 0, nullptr, nullptr);
    cleanup();
    if (status != success)
        return -10000 + status;

    for (uint32_t index = 0; index < item_count; ++index)
    {
        const auto expected = ethash::calculate_dataset_item_1024(*cpu_context, index);
        if (std::memcmp(expected.bytes, output[index].bytes, sizeof(expected.bytes)) != 0)
        {
            *first_mismatch = index;
            return 1;
        }
    }
    return 0;
}

int32_t trmadenci_create_etchash_opencl_epoch(
    const int32_t block_number,
    const int32_t platform_index,
    const int32_t device_index,
    trmadenci_opencl_etchash_epoch** epoch,
    trmadenci_opencl_epoch_build_info* build_info)
{
    constexpr int32_t activation_block = 11'700'000;
    constexpr int32_t legacy_epoch_length = 30'000;
    constexpr int32_t etchash_epoch_length = 60'000;
    constexpr size_t batch_nodes = 16'384;
    if (block_number < 0 || platform_index < 0 || device_index < 0 || epoch == nullptr ||
        build_info == nullptr || !api().runtime_available())
        return -1;
    *epoch = nullptr;
    *build_info = {};
    const auto* selected = find_device(platform_index, device_index);
    if (selected == nullptr)
        return -1;

    const auto epoch_length = block_number < activation_block ? legacy_epoch_length : etchash_epoch_length;
    const auto dataset_epoch = block_number / epoch_length;
    const auto seed_epoch = dataset_epoch * (epoch_length / legacy_epoch_length);
    ethash::epoch_context_ptr cpu_context{
        ethash_create_epoch_context_configured(dataset_epoch, seed_epoch, 256),
        ethash_destroy_epoch_context};
    if (!cpu_context)
        return -2;
    const auto light_bytes = ethash::get_light_cache_size(cpu_context->light_cache_num_items);
    const auto dataset_bytes = ethash::get_full_dataset_size(cpu_context->full_dataset_num_items);
    const auto total_nodes_64 = static_cast<uint64_t>(cpu_context->full_dataset_num_items) * 2;
    if (total_nodes_64 > std::numeric_limits<uint32_t>::max())
        return -3;
    cl_ulong device_memory_bytes = 0;
    if (api().get_device_info(
        selected->device, device_global_mem_size, sizeof(device_memory_bytes),
        &device_memory_bytes, nullptr) != success)
        return -4;
    constexpr uint64_t safety_margin = 256ULL * 1024 * 1024;
    if (dataset_bytes + light_bytes + safety_margin > device_memory_bytes)
        return -4;

    auto& opencl = api();
    cl_int status = success;
    auto context = opencl.create_context(nullptr, 1, &selected->device, nullptr, nullptr, &status);
    cl_command_queue queue = nullptr;
    cl_program program = nullptr;
    cl_kernel kernel = nullptr;
    cl_kernel seed_kernel = nullptr;
    cl_kernel mix_kernel = nullptr;
    cl_kernel final_kernel = nullptr;
    cl_mem light_buffer = nullptr;
    cl_mem dataset_buffer = nullptr;
    const auto cleanup = [&]()
    {
        if (dataset_buffer != nullptr) opencl.release_mem_object(dataset_buffer);
        if (light_buffer != nullptr) opencl.release_mem_object(light_buffer);
        if (final_kernel != nullptr) opencl.release_kernel(final_kernel);
        if (mix_kernel != nullptr) opencl.release_kernel(mix_kernel);
        if (seed_kernel != nullptr) opencl.release_kernel(seed_kernel);
        if (kernel != nullptr) opencl.release_kernel(kernel);
        if (program != nullptr) opencl.release_program(program);
        if (queue != nullptr) opencl.release_command_queue(queue);
        if (context != nullptr) opencl.release_context(context);
    };
    if (status == success)
        queue = opencl.create_command_queue(context, selected->device, 0, &status);
    const char* source_pointer = etchash_dag_source;
    if (status == success)
        program = opencl.create_program_with_source(context, 1, &source_pointer, nullptr, &status);
    if (status == success)
        status = opencl.build_program(program, 1, &selected->device, nullptr, nullptr, nullptr);
    if (status == success)
        kernel = opencl.create_kernel(program, "generate_etchash_nodes", &status);
    if (status == success)
        light_buffer = opencl.create_buffer(context, mem_read_only, light_bytes, nullptr, &status);
    if (status == success)
        dataset_buffer = opencl.create_buffer(
            context, mem_read_write, static_cast<size_t>(dataset_bytes), nullptr, &status);
    if (status == success)
        status = opencl.enqueue_write_buffer(
            queue, light_buffer, true_value, 0, light_bytes,
            cpu_context->light_cache, 0, nullptr, nullptr);
    const auto light_items = static_cast<uint32_t>(cpu_context->light_cache_num_items);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 0, sizeof(light_buffer), &light_buffer);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 1, sizeof(light_items), &light_items);
    if (status == success)
        status = opencl.set_kernel_arg(kernel, 2, sizeof(dataset_buffer), &dataset_buffer);

    const auto started = std::chrono::steady_clock::now();
    const auto total_nodes = static_cast<uint32_t>(total_nodes_64);
    for (uint32_t start_node = 0; status == success && start_node < total_nodes;)
    {
        const auto remaining = total_nodes - start_node;
        const auto nodes_this_batch = static_cast<uint32_t>(std::min<uint64_t>(remaining, batch_nodes));
        const auto global_size = static_cast<size_t>(nodes_this_batch);
        status = opencl.set_kernel_arg(kernel, 3, sizeof(start_node), &start_node);
        if (status == success)
            status = opencl.set_kernel_arg(kernel, 4, sizeof(nodes_this_batch), &nodes_this_batch);
        if (status == success)
            status = opencl.enqueue_ndrange_kernel(
                queue, kernel, 1, nullptr, &global_size, nullptr, 0, nullptr, nullptr);
        start_node += nodes_this_batch;
    }
    const uint32_t validation_indices[] = {
        0,
        static_cast<uint32_t>(cpu_context->full_dataset_num_items / 2),
        static_cast<uint32_t>(cpu_context->full_dataset_num_items - 1)};
    for (const auto index : validation_indices)
    {
        ethash::hash1024 actual{};
        if (status == success)
            status = opencl.enqueue_read_buffer(
                queue, dataset_buffer, true_value,
                static_cast<size_t>(index) * sizeof(actual), sizeof(actual),
                &actual, 0, nullptr, nullptr);
        if (status == success)
        {
            const auto expected = ethash::calculate_dataset_item_1024(*cpu_context, index);
            if (std::memcmp(actual.bytes, expected.bytes, sizeof(actual.bytes)) != 0)
                status = -1;
        }
    }
    const auto finished = std::chrono::steady_clock::now();
    if (light_buffer != nullptr)
    {
        opencl.release_mem_object(light_buffer);
        light_buffer = nullptr;
    }
    if (kernel != nullptr)
    {
        opencl.release_kernel(kernel);
        kernel = nullptr;
    }
    if (status == success)
        seed_kernel = opencl.create_kernel(program, "etchash_seed", &status);
    if (status == success)
        mix_kernel = opencl.create_kernel(program, "etchash_mix", &status);
    if (status == success)
        final_kernel = opencl.create_kernel(program, "etchash_final", &status);
    if (status != success)
    {
        cleanup();
        return -10000 + status;
    }

    auto* result = new (std::nothrow) trmadenci_opencl_etchash_epoch{};
    if (result == nullptr)
    {
        cleanup();
        return -4;
    }
    result->platform_index = platform_index;
    result->device_index = device_index;
    result->epoch_number = dataset_epoch;
    result->full_dataset_items = static_cast<uint32_t>(cpu_context->full_dataset_num_items);
    result->dataset_bytes = dataset_bytes;
    result->context = context;
    result->queue = queue;
    result->program = program;
    result->dataset = dataset_buffer;
    result->seed_kernel = seed_kernel;
    result->mix_kernel = mix_kernel;
    result->final_kernel = final_kernel;
    context = nullptr;
    queue = nullptr;
    program = nullptr;
    dataset_buffer = nullptr;
    seed_kernel = nullptr;
    mix_kernel = nullptr;
    final_kernel = nullptr;
    build_info->epoch_number = dataset_epoch;
    build_info->platform_index = platform_index;
    build_info->device_index = device_index;
    build_info->dataset_bytes = dataset_bytes;
    build_info->build_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    *epoch = result;
    return 0;
}

int32_t trmadenci_search_etchash_opencl(
    trmadenci_opencl_etchash_epoch* epoch,
    const int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    constexpr int32_t activation_block = 11'700'000;
    const auto epoch_length = block_number < activation_block ? 30'000 : 60'000;
    if (epoch == nullptr || block_number < 0 || header_hash == nullptr || target == nullptr ||
        nonce_count == 0 || result == nullptr || block_number / epoch_length != epoch->epoch_number)
        return -1;
    *result = {};
    auto& opencl = api();
    auto status = ensure_opencl_search_buffers(epoch, nonce_count);
    constexpr cl_uint not_found = 0;
    if (status == success)
        status = opencl.enqueue_write_buffer(
            epoch->queue, epoch->search_header, true_value, 0, 32,
            header_hash, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_write_buffer(
            epoch->queue, epoch->search_target, true_value, 0, 32,
            target, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_write_buffer(
            epoch->queue, epoch->search_found, true_value, 0, sizeof(not_found),
            &not_found, 0, nullptr, nullptr);

    const auto kernel_start_nonce = static_cast<cl_ulong>(start_nonce);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->seed_kernel, 0, sizeof(epoch->search_header), &epoch->search_header);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->seed_kernel, 1, sizeof(kernel_start_nonce), &kernel_start_nonce);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->seed_kernel, 2, sizeof(nonce_count), &nonce_count);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->seed_kernel, 3, sizeof(epoch->search_initial), &epoch->search_initial);

    if (status == success)
        status = opencl.set_kernel_arg(epoch->mix_kernel, 0, sizeof(epoch->dataset), &epoch->dataset);
    if (status == success)
        status = opencl.set_kernel_arg(
            epoch->mix_kernel, 1, sizeof(epoch->full_dataset_items), &epoch->full_dataset_items);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->mix_kernel, 2, sizeof(epoch->search_initial), &epoch->search_initial);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->mix_kernel, 3, sizeof(nonce_count), &nonce_count);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->mix_kernel, 4, sizeof(epoch->search_reduced), &epoch->search_reduced);

    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 0, sizeof(epoch->search_initial), &epoch->search_initial);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 1, sizeof(epoch->search_reduced), &epoch->search_reduced);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 2, sizeof(epoch->search_target), &epoch->search_target);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 3, sizeof(kernel_start_nonce), &kernel_start_nonce);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 4, sizeof(nonce_count), &nonce_count);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 5, sizeof(epoch->search_found), &epoch->search_found);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 6, sizeof(epoch->search_nonce), &epoch->search_nonce);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 7, sizeof(epoch->search_mix), &epoch->search_mix);
    if (status == success)
        status = opencl.set_kernel_arg(epoch->final_kernel, 8, sizeof(epoch->search_hash), &epoch->search_hash);

    const auto global_size = static_cast<size_t>(nonce_count);
    const auto started = std::chrono::steady_clock::now();
    if (status == success)
        status = opencl.enqueue_ndrange_kernel(
            epoch->queue, epoch->seed_kernel, 1, nullptr, &global_size, nullptr, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_ndrange_kernel(
            epoch->queue, epoch->mix_kernel, 1, nullptr, &global_size, nullptr, 0, nullptr, nullptr);
    if (status == success)
        status = opencl.enqueue_ndrange_kernel(
            epoch->queue, epoch->final_kernel, 1, nullptr, &global_size, nullptr, 0, nullptr, nullptr);
    cl_uint solution_found = 0;
    if (status == success)
        status = opencl.enqueue_read_buffer(
            epoch->queue, epoch->search_found, true_value, 0, sizeof(solution_found),
            &solution_found, 0, nullptr, nullptr);
    const auto finished = std::chrono::steady_clock::now();
    if (status == success && solution_found != 0)
        status = opencl.enqueue_read_buffer(
            epoch->queue, epoch->search_nonce, true_value, 0, sizeof(result->nonce),
            &result->nonce, 0, nullptr, nullptr);
    if (status == success && solution_found != 0)
        status = opencl.enqueue_read_buffer(
            epoch->queue, epoch->search_mix, true_value, 0, sizeof(result->mix_hash),
            result->mix_hash, 0, nullptr, nullptr);
    if (status == success && solution_found != 0)
        status = opencl.enqueue_read_buffer(
            epoch->queue, epoch->search_hash, true_value, 0, sizeof(result->final_hash),
            result->final_hash, 0, nullptr, nullptr);
    if (status != success)
        return -10000 + status;
    result->solution_found = solution_found != 0 ? 1 : 0;
    result->hashes_searched = nonce_count;
    result->search_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    return 0;
}

void trmadenci_destroy_etchash_opencl_epoch(trmadenci_opencl_etchash_epoch* epoch)
{
    if (epoch == nullptr)
        return;
    auto& opencl = api();
    if (epoch->search_hash != nullptr) opencl.release_mem_object(epoch->search_hash);
    if (epoch->search_mix != nullptr) opencl.release_mem_object(epoch->search_mix);
    if (epoch->search_nonce != nullptr) opencl.release_mem_object(epoch->search_nonce);
    if (epoch->search_found != nullptr) opencl.release_mem_object(epoch->search_found);
    if (epoch->search_reduced != nullptr) opencl.release_mem_object(epoch->search_reduced);
    if (epoch->search_initial != nullptr) opencl.release_mem_object(epoch->search_initial);
    if (epoch->search_target != nullptr) opencl.release_mem_object(epoch->search_target);
    if (epoch->search_header != nullptr) opencl.release_mem_object(epoch->search_header);
    if (epoch->final_kernel != nullptr) opencl.release_kernel(epoch->final_kernel);
    if (epoch->mix_kernel != nullptr) opencl.release_kernel(epoch->mix_kernel);
    if (epoch->seed_kernel != nullptr) opencl.release_kernel(epoch->seed_kernel);
    if (epoch->dataset != nullptr) opencl.release_mem_object(epoch->dataset);
    if (epoch->program != nullptr) opencl.release_program(epoch->program);
    if (epoch->queue != nullptr) opencl.release_command_queue(epoch->queue);
    if (epoch->context != nullptr) opencl.release_context(epoch->context);
    delete epoch;
}
#else
int32_t trmadenci_get_opencl_device_count() { return 0; }
int32_t trmadenci_get_opencl_device_info(int32_t, trmadenci_opencl_device_info*) { return -1; }
int32_t trmadenci_opencl_self_test(int32_t, int32_t, uint32_t*) { return -1; }
int32_t trmadenci_validate_etchash_opencl_dag_items(
    int32_t, int32_t, int32_t, uint32_t, uint32_t*) { return -1; }
int32_t trmadenci_create_etchash_opencl_epoch(
    int32_t, int32_t, int32_t, trmadenci_opencl_etchash_epoch**, trmadenci_opencl_epoch_build_info*)
{
    return -1;
}
void trmadenci_destroy_etchash_opencl_epoch(trmadenci_opencl_etchash_epoch*) {}
int32_t trmadenci_search_etchash_opencl(
    trmadenci_opencl_etchash_epoch*, int32_t, const uint8_t[32], const uint8_t[32],
    uint64_t, uint32_t, trmadenci_search_result*) { return -1; }
#endif
