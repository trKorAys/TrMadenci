#include "trmadenci_native.h"

#include <cuda_runtime_api.h>
#include <nvml.h>

#include <mutex>

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4996)
#endif

namespace
{
std::once_flag nvml_once;
nvmlReturn_t nvml_initialization = NVML_ERROR_UNINITIALIZED;

void initialize_nvml()
{
    nvml_initialization = nvmlInit_v2();
}

bool succeeded(const nvmlReturn_t status)
{
    return status == NVML_SUCCESS;
}
}

int32_t trmadenci_get_gpu_telemetry(
    const int32_t device_index,
    trmadenci_gpu_telemetry* telemetry)
{
    if (telemetry == nullptr || device_index < 0)
        return -1;

    *telemetry = {};
    std::call_once(nvml_once, initialize_nvml);
    if (!succeeded(nvml_initialization))
        return -1000 - static_cast<int32_t>(nvml_initialization);

    char pci_bus_id[32]{};
    const auto cuda_status = cudaDeviceGetPCIBusId(pci_bus_id, sizeof(pci_bus_id), device_index);
    if (cuda_status != cudaSuccess)
        return -2000 - static_cast<int32_t>(cuda_status);

    nvmlDevice_t device{};
    const auto handle_status = nvmlDeviceGetHandleByPciBusId_v2(pci_bus_id, &device);
    if (!succeeded(handle_status))
        return -3000 - static_cast<int32_t>(handle_status);

    unsigned int value = 0;
    if (succeeded(nvmlDeviceGetTemperature(device, NVML_TEMPERATURE_GPU, &value)))
    {
        telemetry->temperature_c = value;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_TEMPERATURE;
    }

    if (succeeded(nvmlDeviceGetFanSpeed(device, &value)))
    {
        telemetry->fan_percent = value;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_FAN;
    }

    if (succeeded(nvmlDeviceGetPowerUsage(device, &value)))
    {
        telemetry->power_milliwatts = value;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_POWER;
    }

    nvmlUtilization_t utilization{};
    if (succeeded(nvmlDeviceGetUtilizationRates(device, &utilization)))
    {
        telemetry->gpu_utilization_percent = utilization.gpu;
        telemetry->memory_utilization_percent = utilization.memory;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_UTILIZATION;
    }

    nvmlMemory_t memory{};
    if (succeeded(nvmlDeviceGetMemoryInfo(device, &memory)))
    {
        telemetry->memory_used_bytes = memory.used;
        telemetry->memory_total_bytes = memory.total;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_MEMORY;
    }

    if (succeeded(nvmlDeviceGetClockInfo(device, NVML_CLOCK_GRAPHICS, &value)))
    {
        telemetry->graphics_clock_mhz = value;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_GRAPHICS_CLOCK;
    }

    if (succeeded(nvmlDeviceGetClockInfo(device, NVML_CLOCK_MEM, &value)))
    {
        telemetry->memory_clock_mhz = value;
        telemetry->valid_fields |= TRMADENCI_TELEMETRY_MEMORY_CLOCK;
    }

    return 0;
}

#if defined(_MSC_VER)
#pragma warning(pop)
#endif
