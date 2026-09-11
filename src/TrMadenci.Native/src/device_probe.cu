#include "trmadenci_native.h"

#include <cuda_runtime_api.h>

#include <algorithm>
#include <cstring>
#include <string>

namespace
{
thread_local std::string last_error;

int32_t fail(cudaError_t error)
{
    last_error = cudaGetErrorString(error);
    return -static_cast<int32_t>(error);
}
}

int32_t trmadenci_get_device_count()
{
    int count = 0;
    const auto result = cudaGetDeviceCount(&count);
    if (result != cudaSuccess)
        return fail(result);

    last_error.clear();
    return count;
}

int32_t trmadenci_get_device_info(const int32_t index, trmadenci_device_info* info)
{
    if (info == nullptr)
    {
        last_error = "Device info output pointer is null.";
        return -1;
    }

    cudaDeviceProp properties{};
    const auto result = cudaGetDeviceProperties(&properties, index);
    if (result != cudaSuccess)
        return fail(result);

    *info = {};
    info->index = index;
    std::strncpy(info->name, properties.name, sizeof(info->name) - 1);
    info->total_memory_bytes = properties.totalGlobalMem;
    info->compute_major = properties.major;
    info->compute_minor = properties.minor;
    last_error.clear();
    return 0;
}

const char* trmadenci_get_last_error()
{
    return last_error.c_str();
}
