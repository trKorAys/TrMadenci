// KAWPOW CUDA nonce search independently implemented against the Apache-2.0
// cpp-kawpow correctness oracle. Each nonce is evaluated by a cooperative
// 16-thread CUDA group matching ProgPoW's logical lane layout.

#include "trmadenci_native.h"
#include "cuda_epoch_context.hpp"

#include <cuda_runtime_api.h>
#include <cuda.h>
#include <nvrtc.h>

#include <chrono>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <string>
#include <vector>

namespace
{
int32_t cuda_failure(cudaError_t status);

constexpr uint32_t fnv_prime = 0x01000193u;
constexpr uint32_t fnv_offset = 0x811c9dc5u;
constexpr uint32_t regs = 32;
constexpr uint32_t lanes = 16;
constexpr uint32_t l1_words = 16 * 1024 / sizeof(uint32_t);

__device__ __forceinline__ uint32_t rotate_left(const uint32_t value, const uint32_t shift)
{
    const auto amount = shift & 31;
    return amount == 0 ? value : (value << amount) | (value >> (32 - amount));
}

__device__ __forceinline__ uint32_t rotate_right(const uint32_t value, const uint32_t shift)
{
    const auto amount = shift & 31;
    return amount == 0 ? value : (value >> amount) | (value << (32 - amount));
}

__device__ void keccak_f800(uint32_t state[25])
{
    constexpr uint32_t round_constants[22] = {
        0x00000001u, 0x00008082u, 0x0000808au, 0x80008000u, 0x0000808bu, 0x80000001u,
        0x80008081u, 0x00008009u, 0x0000008au, 0x00000088u, 0x80008009u, 0x8000000au,
        0x8000808bu, 0x0000008bu, 0x00008089u, 0x00008003u, 0x00008002u, 0x00000080u,
        0x0000800au, 0x8000000au, 0x80008081u, 0x00008080u};
    constexpr unsigned rho[25] = {
        0, 1, 30, 28, 27,
        4, 12, 6, 23, 20,
        3, 10, 11, 25, 7,
        9, 13, 15, 21, 8,
        18, 2, 29, 24, 14};

    for (const auto round_constant : round_constants)
    {
        uint32_t column[5];
        uint32_t delta[5];
        uint32_t rotated[25];
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

__device__ __forceinline__ uint64_t rotate_left64(const uint64_t value, const unsigned shift)
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
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43,
        25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14};

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
            delta[x] = column[(x + 4) % 5] ^ rotate_left64(column[(x + 1) % 5], 1);
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
                    rotate_left64(state[x + 5 * y], rho[x + 5 * y]);
#pragma unroll
        for (int y = 0; y < 5; ++y)
#pragma unroll
            for (int x = 0; x < 5; ++x)
                state[x + 5 * y] = rotated[x + 5 * y] ^
                    ((~rotated[(x + 1) % 5 + 5 * y]) & rotated[(x + 2) % 5 + 5 * y]);
        state[0] ^= round_constant;
    }
}

struct kiss_state
{
    uint32_t z;
    uint32_t w;
    uint32_t jsr;
    uint32_t jcong;

    __device__ uint32_t next()
    {
        z = 36969 * (z & 0xffff) + (z >> 16);
        w = 18000 * (w & 0xffff) + (w >> 16);
        jcong = 69069 * jcong + 1234567;
        jsr ^= jsr << 17;
        jsr ^= jsr >> 13;
        jsr ^= jsr << 5;
        return (((z << 16) + w) ^ jcong) + jsr;
    }
};

__device__ __forceinline__ uint32_t fnv1a(const uint32_t a, const uint32_t b)
{
    return (a ^ b) * fnv_prime;
}

__device__ __forceinline__ uint32_t random_math(
    const uint32_t a, const uint32_t b, const uint32_t selector)
{
    switch (selector % 11)
    {
    case 0: return a + b;
    case 1: return a * b;
    case 2: return __umulhi(a, b);
    case 3: return a < b ? a : b;
    case 4: return rotate_left(a, b);
    case 5: return rotate_right(a, b);
    case 6: return a & b;
    case 7: return a | b;
    case 8: return a ^ b;
    case 9: return static_cast<uint32_t>(a == 0 ? 32 : __clz(a)) + (b == 0 ? 32 : __clz(b));
    default: return __popc(a) + __popc(b);
    }
}

__device__ __forceinline__ void random_merge(uint32_t& a, const uint32_t b, const uint32_t selector)
{
    const auto rotation = (selector >> 16) % 31 + 1;
    switch (selector % 4)
    {
    case 0: a = a * 33 + b; break;
    case 1: a = (a ^ b) * 33; break;
    case 2: a = rotate_left(a, rotation) ^ b; break;
    default: a = rotate_right(a, rotation) ^ b; break;
    }
}

struct program_state
{
    kiss_state rng;
    uint32_t destinations[regs];
    uint32_t sources[regs];
    uint32_t destination_counter;
    uint32_t source_counter;

    __device__ explicit program_state(const uint64_t seed)
    {
        const auto seed_low = static_cast<uint32_t>(seed);
        const auto seed_high = static_cast<uint32_t>(seed >> 32);
        const auto z = fnv1a(fnv_offset, seed_low);
        const auto w = fnv1a(z, seed_high);
        const auto jsr = fnv1a(w, seed_low);
        const auto jcong = fnv1a(jsr, seed_high);
        rng = {z, w, jsr, jcong};
        destination_counter = 0;
        source_counter = 0;
#pragma unroll
        for (uint32_t i = 0; i < regs; ++i)
        {
            destinations[i] = i;
            sources[i] = i;
        }
        for (uint32_t i = regs; i > 1; --i)
        {
            auto position = rng.next() % i;
            auto temporary = destinations[i - 1];
            destinations[i - 1] = destinations[position];
            destinations[position] = temporary;
            position = rng.next() % i;
            temporary = sources[i - 1];
            sources[i - 1] = sources[position];
            sources[position] = temporary;
        }
    }

    __device__ uint32_t next_destination() { return destinations[(destination_counter++) % regs]; }
    __device__ uint32_t next_source() { return sources[(source_counter++) % regs]; }
};

struct cache_instruction
{
    uint32_t source;
    uint32_t destination;
    uint32_t selector;
};

struct math_instruction
{
    uint32_t source1;
    uint32_t source2;
    uint32_t selector1;
    uint32_t destination;
    uint32_t selector2;
};

struct kawpow_program
{
    cache_instruction cache[11];
    math_instruction math[18];
    uint32_t dag_destinations[4];
    uint32_t dag_selectors[4];
};

struct host_kiss_state
{
    uint32_t z;
    uint32_t w;
    uint32_t jsr;
    uint32_t jcong;

    uint32_t next()
    {
        z = 36969 * (z & 0xffff) + (z >> 16);
        w = 18000 * (w & 0xffff) + (w >> 16);
        jcong = 69069 * jcong + 1234567;
        jsr ^= jsr << 17;
        jsr ^= jsr >> 13;
        jsr ^= jsr << 5;
        return (((z << 16) + w) ^ jcong) + jsr;
    }
};

uint32_t host_fnv1a(const uint32_t a, const uint32_t b)
{
    return (a ^ b) * fnv_prime;
}

kawpow_program build_program(const int32_t block_number)
{
    const auto seed = static_cast<uint64_t>(block_number / 3);
    const auto seed_low = static_cast<uint32_t>(seed);
    const auto seed_high = static_cast<uint32_t>(seed >> 32);
    const auto z = host_fnv1a(fnv_offset, seed_low);
    const auto w = host_fnv1a(z, seed_high);
    const auto jsr = host_fnv1a(w, seed_low);
    const auto jcong = host_fnv1a(jsr, seed_high);
    host_kiss_state rng{z, w, jsr, jcong};
    uint32_t destinations[regs];
    uint32_t sources[regs];
    for (uint32_t i = 0; i < regs; ++i)
        destinations[i] = sources[i] = i;
    for (uint32_t i = regs; i > 1; --i)
    {
        auto position = rng.next() % i;
        auto temporary = destinations[i - 1];
        destinations[i - 1] = destinations[position];
        destinations[position] = temporary;
        position = rng.next() % i;
        temporary = sources[i - 1];
        sources[i - 1] = sources[position];
        sources[position] = temporary;
    }

    uint32_t destination_counter = 0;
    uint32_t source_counter = 0;
    const auto next_destination = [&]() { return destinations[(destination_counter++) % regs]; };
    const auto next_source = [&]() { return sources[(source_counter++) % regs]; };

    kawpow_program program{};
    for (uint32_t operation = 0; operation < 18; ++operation)
    {
        if (operation < 11)
            program.cache[operation] = {next_source(), next_destination(), rng.next()};

        const auto source_random = rng.next() % (regs * (regs - 1));
        const auto source1 = source_random % regs;
        auto source2 = source_random / regs;
        if (source2 >= source1)
            ++source2;
        program.math[operation] = {
            source1, source2, rng.next(), next_destination(), rng.next()};
    }
    for (uint32_t word = 0; word < 4; ++word)
    {
        program.dag_destinations[word] = word == 0 ? 0 : next_destination();
        program.dag_selectors[word] = rng.next();
    }
    return program;
}

std::string register_name(const uint32_t index)
{
    return "r" + std::to_string(index);
}

std::string build_jit_source(const kawpow_program& program)
{
    std::ostringstream source;
    source << R"CUDA(
typedef unsigned int u32;
typedef unsigned long long u64;
struct search_result {
    int solution_found;
    u64 nonce;
    unsigned char mix_hash[32];
    unsigned char final_hash[32];
    u64 hashes_searched;
    double search_milliseconds;
};
__device__ __forceinline__ u32 rotl(u32 v, u32 s) { s &= 31; return s ? (v << s) | (v >> (32-s)) : v; }
__device__ __forceinline__ u32 rotr(u32 v, u32 s) { s &= 31; return s ? (v >> s) | (v << (32-s)) : v; }
__device__ __forceinline__ u32 fnv(u32 a, u32 b) { return (a ^ b) * 0x01000193u; }
__device__ __forceinline__ u32 rng_next(u32& z, u32& w, u32& jsr, u32& jcong) {
    z = 36969u * (z & 0xffffu) + (z >> 16); w = 18000u * (w & 0xffffu) + (w >> 16);
    jcong = 69069u * jcong + 1234567u; jsr ^= jsr << 17; jsr ^= jsr >> 13; jsr ^= jsr << 5;
    return (((z << 16) + w) ^ jcong) + jsr;
}
__device__ __forceinline__ u32 math_op(u32 a, u32 b, u32 s) {
    switch (s % 11u) {
    case 0: return a+b; case 1: return a*b; case 2: return __umulhi(a,b); case 3: return a<b?a:b;
    case 4: return rotl(a,b); case 5: return rotr(a,b); case 6: return a&b; case 7: return a|b;
    case 8: return a^b; case 9: return (a?__clz(a):32)+(b?__clz(b):32); default: return __popc(a)+__popc(b); }
}
__device__ __forceinline__ void merge(u32& a, u32 b, u32 s) {
    u32 rotation = (s >> 16) % 31u + 1u;
    switch (s % 4u) { case 0: a=a*33u+b; break; case 1: a=(a^b)*33u; break;
    case 2: a=rotl(a,rotation)^b; break; default: a=rotr(a,rotation)^b; break; }
}
__device__ void keccak(u32 st[25]) {
    const u32 rc[22]={0x1u,0x8082u,0x808au,0x80008000u,0x808bu,0x80000001u,0x80008081u,0x8009u,0x8au,0x88u,0x80008009u,0x8000000au,0x8000808bu,0x8bu,0x8089u,0x8003u,0x8002u,0x80u,0x800au,0x8000000au,0x80008081u,0x8080u};
    const int rho[25]={0,1,30,28,27,4,12,6,23,20,3,10,11,25,7,9,13,15,21,8,18,2,29,24,14};
    #pragma unroll
    for(int r=0;r<22;r++){u32 c[5],d[5],b[25];
      #pragma unroll
      for(int x=0;x<5;x++)c[x]=st[x]^st[x+5]^st[x+10]^st[x+15]^st[x+20];
      #pragma unroll
      for(int x=0;x<5;x++)d[x]=c[(x+4)%5]^rotl(c[(x+1)%5],1);
      #pragma unroll
      for(int y=0;y<5;y++)for(int x=0;x<5;x++)st[x+5*y]^=d[x];
      #pragma unroll
      for(int y=0;y<5;y++)for(int x=0;x<5;x++)b[y+5*((2*x+3*y)%5)]=rotl(st[x+5*y],rho[x+5*y]);
      #pragma unroll
      for(int y=0;y<5;y++)for(int x=0;x<5;x++)st[x+5*y]=b[x+5*y]^((~b[(x+1)%5+5*y])&b[(x+2)%5+5*y]);
      st[0]^=rc[r];}
}
__device__ __forceinline__ bool meets(const u32 h[8], const unsigned char* t) {
    #pragma unroll
    for(int i=0;i<32;i++){unsigned char b=(unsigned char)(h[i/4]>>((i%4)*8)); if(b<t[i])return true; if(b>t[i])return false;} return true;
}
__device__ __constant__ u32 raven[15]={0x72,0x41,0x56,0x45,0x4e,0x43,0x4f,0x49,0x4e,0x4b,0x41,0x57,0x50,0x4f,0x57};
extern "C" __global__ void kawpow_seed(const u32* header, u64 start_nonce, u32 nonce_count, u32* initial) {
    u32 ni=blockIdx.x*blockDim.x+threadIdx.x; if(ni>=nonce_count)return; u64 nonce=start_nonce+ni;
    u32 st[25]={0}; for(int i=0;i<8;i++)st[i]=header[i]; st[8]=(u32)nonce; st[9]=(u32)(nonce>>32);
    for(int i=10;i<25;i++)st[i]=raven[i-10]; keccak(st); for(int i=0;i<8;i++)initial[ni*8u+i]=st[i];
}
extern "C" __global__ void kawpow_mix(const u32* dataset, u32 full_items,
    const u32* initial, u32 nonce_count, u32* reduced) {
    u32 gt=blockIdx.x*blockDim.x+threadIdx.x, ni=gt/16u, lane=threadIdx.x&15u;
    u32 wl=threadIdx.x&31u, mask=wl<16?0xffffu:0xffff0000u;
    if(ni>=nonce_count)return;
    u32 z=fnv(0x811c9dc5u,initial[ni*8u]),w=fnv(z,initial[ni*8u+1u]),jsr=fnv(w,lane),jcong=fnv(jsr,lane);
)CUDA";

    for (uint32_t reg = 0; reg < regs; ++reg)
        source << "u32 r" << reg << "=rng_next(z,w,jsr,jcong);\n";

    source << "for(u32 round=0;round<64u;round++){\n"
              "u32 item_index=__shfl_sync(mask,r0,(int)(round&15u),16)%(full_items/2u);\n"
              "const u32* item=dataset+(u64)item_index*64u;\n";
    for (uint32_t operation = 0; operation < 18; ++operation)
    {
        if (operation < 11)
        {
            const auto& instruction = program.cache[operation];
            source << "merge(" << register_name(instruction.destination) << ",dataset["
                   << register_name(instruction.source) << "%4096u],"
                   << instruction.selector << "u);\n";
        }
        const auto& instruction = program.math[operation];
        source << "merge(" << register_name(instruction.destination) << ",math_op("
               << register_name(instruction.source1) << ',' << register_name(instruction.source2)
               << ',' << instruction.selector1 << "u)," << instruction.selector2 << "u);\n";
    }
    source << "u32 off=((lane^round)&15u)*4u;\n";
    for (uint32_t word = 0; word < 4; ++word)
        source << "merge(" << register_name(program.dag_destinations[word]) << ",item[off+"
               << word << "u]," << program.dag_selectors[word] << "u);\n";
    source << "}\nu32 lh=0x811c9dc5u;\n";
    for (uint32_t reg = 0; reg < regs; ++reg)
        source << "lh=fnv(lh,r" << reg << ");\n";
    source << R"CUDA(
    u32 pair=__shfl_sync(mask,lh,(lane+8u)&15u,16);
    if(lane<8)reduced[ni*8u+lane]=fnv(fnv(0x811c9dc5u,lh),pair);
}
extern "C" __global__ void kawpow_final(const u32* initial, const u32* reduced,
    const unsigned char* target, u64 start_nonce, u32 nonce_count, search_result* result) {
    u32 ni=blockIdx.x*blockDim.x+threadIdx.x; if(ni>=nonce_count || result->solution_found)return;
    u32 out[25]={0}; for(int i=0;i<8;i++){out[i]=initial[ni*8u+i];out[i+8]=reduced[ni*8u+i];}
    for(int i=16;i<25;i++)out[i]=raven[i-16]; keccak(out);
    if(!meets(out,target)||atomicCAS(&result->solution_found,0,1))return;
    result->nonce=start_nonce+ni;
    for(int i=0;i<8;i++){((u32*)result->mix_hash)[i]=reduced[ni*8u+i];((u32*)result->final_hash)[i]=out[i];}
}
)CUDA";
    return source.str();
}

int32_t ensure_jit_program(trmadenci_cuda_epoch* epoch, const int32_t block_number)
{
    const auto program_seed = block_number / 3;
    if (epoch->jit_program_seed == program_seed && epoch->jit_mix_function != nullptr)
        return 0;

    const auto source = build_jit_source(build_program(block_number));
    nvrtcProgram compiler_program = nullptr;
    auto nvrtc_status = nvrtcCreateProgram(
        &compiler_program, source.c_str(), "trmadenci_kawpow_jit.cu", 0, nullptr, nullptr);
    if (nvrtc_status != NVRTC_SUCCESS)
        return -3000 - static_cast<int32_t>(nvrtc_status);

    cudaDeviceProp properties{};
    auto cuda_status = cudaGetDeviceProperties(&properties, epoch->device_index);
    if (cuda_status != cudaSuccess)
    {
        nvrtcDestroyProgram(&compiler_program);
        return cuda_failure(cuda_status);
    }
    const auto architecture = "--gpu-architecture=compute_" +
        std::to_string(properties.major) + std::to_string(properties.minor);
    const char* options[] = {architecture.c_str(), "--std=c++17", "--use_fast_math"};
    nvrtc_status = nvrtcCompileProgram(compiler_program, 3, options);
    if (nvrtc_status != NVRTC_SUCCESS)
    {
        nvrtcDestroyProgram(&compiler_program);
        return -3000 - static_cast<int32_t>(nvrtc_status);
    }

    size_t ptx_size = 0;
    nvrtc_status = nvrtcGetPTXSize(compiler_program, &ptx_size);
    std::vector<char> ptx(ptx_size);
    if (nvrtc_status == NVRTC_SUCCESS)
        nvrtc_status = nvrtcGetPTX(compiler_program, ptx.data());
    nvrtcDestroyProgram(&compiler_program);
    if (nvrtc_status != NVRTC_SUCCESS)
        return -3000 - static_cast<int32_t>(nvrtc_status);

    CUmodule module = nullptr;
    auto driver_status = cuModuleLoadData(&module, ptx.data());
    if (driver_status != CUDA_SUCCESS)
        return -4000 - static_cast<int32_t>(driver_status);
    CUfunction seed_function = nullptr;
    CUfunction mix_function = nullptr;
    CUfunction final_function = nullptr;
    driver_status = cuModuleGetFunction(&seed_function, module, "kawpow_seed");
    if (driver_status == CUDA_SUCCESS)
        driver_status = cuModuleGetFunction(&mix_function, module, "kawpow_mix");
    if (driver_status == CUDA_SUCCESS)
        driver_status = cuModuleGetFunction(&final_function, module, "kawpow_final");
    if (driver_status != CUDA_SUCCESS)
    {
        cuModuleUnload(module);
        return -4000 - static_cast<int32_t>(driver_status);
    }

    if (epoch->jit_module != nullptr)
        cuModuleUnload(reinterpret_cast<CUmodule>(epoch->jit_module));
    epoch->jit_module = reinterpret_cast<void*>(module);
    epoch->jit_seed_function = reinterpret_cast<void*>(seed_function);
    epoch->jit_mix_function = reinterpret_cast<void*>(mix_function);
    epoch->jit_final_function = reinterpret_cast<void*>(final_function);
    epoch->jit_program_seed = program_seed;
    return 0;
}

__device__ void kawpow_hash(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const int32_t block_number,
    const uint32_t header[8],
    const uint64_t nonce,
    uint32_t mix_hash[8],
    uint32_t final_hash[8])
{
    constexpr uint32_t ravencoin[15] = {
        0x72, 0x41, 0x56, 0x45, 0x4e, 0x43, 0x4f, 0x49,
        0x4e, 0x4b, 0x41, 0x57, 0x50, 0x4f, 0x57};
    uint32_t state[25]{};
#pragma unroll
    for (int i = 0; i < 8; ++i)
        state[i] = header[i];
    state[8] = static_cast<uint32_t>(nonce);
    state[9] = static_cast<uint32_t>(nonce >> 32);
#pragma unroll
    for (int i = 10; i < 25; ++i)
        state[i] = ravencoin[i - 10];
    keccak_f800(state);

    uint32_t mix[lanes][regs];
    const auto z = fnv1a(fnv_offset, state[0]);
    const auto w = fnv1a(z, state[1]);
#pragma unroll
    for (uint32_t lane = 0; lane < lanes; ++lane)
    {
        const auto jsr = fnv1a(w, lane);
        const auto jcong = fnv1a(jsr, lane);
        kiss_state rng{z, w, jsr, jcong};
#pragma unroll
        for (uint32_t reg = 0; reg < regs; ++reg)
            mix[lane][reg] = rng.next();
    }

    const auto program_seed = static_cast<uint64_t>(block_number / 3);
    const auto dag_items_256 = full_dataset_items / 2;
    for (uint32_t round = 0; round < 64; ++round)
    {
        program_state program{program_seed};
        const auto item_index = mix[round % lanes][0] % dag_items_256;
        const auto* item = dataset + static_cast<uint64_t>(item_index) * 64;

        for (uint32_t operation = 0; operation < 18; ++operation)
        {
            if (operation < 11)
            {
                const auto source = program.next_source();
                const auto destination = program.next_destination();
                const auto selector = program.rng.next();
#pragma unroll
                for (uint32_t lane = 0; lane < lanes; ++lane)
                    random_merge(mix[lane][destination], dataset[mix[lane][source] % l1_words], selector);
            }

            const auto source_random = program.rng.next() % (regs * (regs - 1));
            const auto source1 = source_random % regs;
            auto source2 = source_random / regs;
            if (source2 >= source1)
                ++source2;
            const auto selector1 = program.rng.next();
            const auto destination = program.next_destination();
            const auto selector2 = program.rng.next();
#pragma unroll
            for (uint32_t lane = 0; lane < lanes; ++lane)
            {
                const auto data = random_math(mix[lane][source1], mix[lane][source2], selector1);
                random_merge(mix[lane][destination], data, selector2);
            }
        }

        uint32_t destinations[4];
        uint32_t selectors[4];
#pragma unroll
        for (uint32_t word = 0; word < 4; ++word)
        {
            destinations[word] = word == 0 ? 0 : program.next_destination();
            selectors[word] = program.rng.next();
        }
#pragma unroll
        for (uint32_t lane = 0; lane < lanes; ++lane)
        {
            const auto offset = ((lane ^ round) % lanes) * 4;
#pragma unroll
            for (uint32_t word = 0; word < 4; ++word)
                random_merge(mix[lane][destinations[word]], item[offset + word], selectors[word]);
        }
    }

    uint32_t lane_hash[lanes];
#pragma unroll
    for (uint32_t lane = 0; lane < lanes; ++lane)
    {
        lane_hash[lane] = fnv_offset;
#pragma unroll
        for (uint32_t reg = 0; reg < regs; ++reg)
            lane_hash[lane] = fnv1a(lane_hash[lane], mix[lane][reg]);
    }
#pragma unroll
    for (uint32_t word = 0; word < 8; ++word)
        mix_hash[word] = fnv_offset;
#pragma unroll
    for (uint32_t lane = 0; lane < lanes; ++lane)
        mix_hash[lane % 8] = fnv1a(mix_hash[lane % 8], lane_hash[lane]);

    uint32_t final_state[25]{};
#pragma unroll
    for (int i = 0; i < 8; ++i)
    {
        final_state[i] = state[i];
        final_state[i + 8] = mix_hash[i];
    }
#pragma unroll
    for (int i = 16; i < 25; ++i)
        final_state[i] = ravencoin[i - 16];
    keccak_f800(final_state);
#pragma unroll
    for (int i = 0; i < 8; ++i)
        final_hash[i] = final_state[i];
}

__device__ bool meets_target(const uint32_t hash[8], const uint8_t target[32])
{
#pragma unroll
    for (int byte_index = 0; byte_index < 32; ++byte_index)
    {
        const auto hash_byte = static_cast<uint8_t>(hash[byte_index / 4] >> ((byte_index % 4) * 8));
        if (hash_byte < target[byte_index])
            return true;
        if (hash_byte > target[byte_index])
            return false;
    }
    return true;
}

[[maybe_unused]] __global__ void etchash_search_kernel(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const uint32_t* header,
    const uint8_t* target,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= nonce_count || result->solution_found != 0)
        return;

    const auto nonce = start_nonce + index;
    uint64_t seed_state[25]{};
#pragma unroll
    for (int word = 0; word < 4; ++word)
        seed_state[word] = static_cast<uint64_t>(header[word * 2]) |
            (static_cast<uint64_t>(header[word * 2 + 1]) << 32);
    seed_state[4] = nonce;
    seed_state[5] = 0x0000000000000001ULL;
    seed_state[8] = 0x8000000000000000ULL;
    keccak_f1600(seed_state);

    uint32_t seed[16];
    uint32_t mix[32];
#pragma unroll
    for (int word = 0; word < 16; ++word)
    {
        seed[word] = static_cast<uint32_t>(seed_state[word / 2] >> ((word & 1) * 32));
        mix[word] = seed[word];
        mix[word + 16] = seed[word];
    }

    const auto seed_head = seed[0];
#pragma unroll 1
    for (uint32_t access = 0; access < 64; ++access)
    {
        const auto item = (((access ^ seed_head) * fnv_prime) ^ mix[access % 32]) %
            full_dataset_items;
        const auto* data = dataset + static_cast<uint64_t>(item) * 32;
#pragma unroll
        for (int word = 0; word < 32; ++word)
            mix[word] = (mix[word] * fnv_prime) ^ data[word];
    }

    uint32_t reduced[8];
#pragma unroll
    for (int word = 0; word < 8; ++word)
    {
        const auto offset = word * 4;
        auto value = (mix[offset] * fnv_prime) ^ mix[offset + 1];
        value = (value * fnv_prime) ^ mix[offset + 2];
        reduced[word] = (value * fnv_prime) ^ mix[offset + 3];
    }

    uint64_t final_state[25]{};
#pragma unroll
    for (int word = 0; word < 8; ++word)
        final_state[word] = seed_state[word];
#pragma unroll
    for (int word = 0; word < 4; ++word)
        final_state[word + 8] = static_cast<uint64_t>(reduced[word * 2]) |
            (static_cast<uint64_t>(reduced[word * 2 + 1]) << 32);
    final_state[12] = 0x0000000000000001ULL;
    final_state[16] = 0x8000000000000000ULL;
    keccak_f1600(final_state);

    uint32_t final_hash[8];
#pragma unroll
    for (int word = 0; word < 8; ++word)
        final_hash[word] = static_cast<uint32_t>(final_state[word / 2] >> ((word & 1) * 32));
    if (!meets_target(final_hash, target) || atomicCAS(&result->solution_found, 0, 1) != 0)
        return;

    result->nonce = nonce;
#pragma unroll
    for (int word = 0; word < 8; ++word)
    {
        reinterpret_cast<uint32_t*>(result->mix_hash)[word] = reduced[word];
        reinterpret_cast<uint32_t*>(result->final_hash)[word] = final_hash[word];
    }
}

__global__ void etchash_seed_kernel(
    const uint32_t* header,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    uint32_t* initial)
{
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= nonce_count)
        return;

    uint64_t state[25]{};
#pragma unroll
    for (int word = 0; word < 4; ++word)
        state[word] = static_cast<uint64_t>(header[word * 2]) |
            (static_cast<uint64_t>(header[word * 2 + 1]) << 32);
    state[4] = start_nonce + index;
    state[5] = 0x0000000000000001ULL;
    state[8] = 0x8000000000000000ULL;
    keccak_f1600(state);

#pragma unroll
    for (int word = 0; word < 16; ++word)
        initial[static_cast<uint64_t>(index) * 16 + word] =
            static_cast<uint32_t>(state[word / 2] >> ((word & 1) * 32));
}

__global__ void etchash_mix_kernel(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const uint32_t* initial,
    const uint32_t nonce_count,
    uint32_t* reduced_output)
{
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= nonce_count)
        return;

    const auto* seed = initial + static_cast<uint64_t>(index) * 16;
    uint32_t mix[32];
#pragma unroll
    for (int word = 0; word < 16; ++word)
    {
        mix[word] = seed[word];
        mix[word + 16] = seed[word];
    }
    const auto seed_head = seed[0];
#pragma unroll 1
    for (uint32_t access = 0; access < 64; ++access)
    {
        const auto item = (((access ^ seed_head) * fnv_prime) ^ mix[access & 31u]) %
            full_dataset_items;
        const auto* data = dataset + static_cast<uint64_t>(item) * 32;
#pragma unroll
        for (int word = 0; word < 32; ++word)
            mix[word] = (mix[word] * fnv_prime) ^ data[word];
    }

    auto* reduced = reduced_output + static_cast<uint64_t>(index) * 8;
#pragma unroll
    for (int word = 0; word < 8; ++word)
    {
        const auto offset = word * 4;
        auto value = (mix[offset] * fnv_prime) ^ mix[offset + 1];
        value = (value * fnv_prime) ^ mix[offset + 2];
        reduced[word] = (value * fnv_prime) ^ mix[offset + 3];
    }
}

__global__ void etchash_final_kernel(
    const uint32_t* initial,
    const uint32_t* reduced,
    const uint8_t* target,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= nonce_count || result->solution_found != 0)
        return;

    const auto* seed = initial + static_cast<uint64_t>(index) * 16;
    const auto* mix_hash = reduced + static_cast<uint64_t>(index) * 8;
    uint64_t state[25]{};
#pragma unroll
    for (int word = 0; word < 8; ++word)
        state[word] = static_cast<uint64_t>(seed[word * 2]) |
            (static_cast<uint64_t>(seed[word * 2 + 1]) << 32);
#pragma unroll
    for (int word = 0; word < 4; ++word)
        state[word + 8] = static_cast<uint64_t>(mix_hash[word * 2]) |
            (static_cast<uint64_t>(mix_hash[word * 2 + 1]) << 32);
    state[12] = 0x0000000000000001ULL;
    state[16] = 0x8000000000000000ULL;
    keccak_f1600(state);

    uint32_t final_hash[8];
#pragma unroll
    for (int word = 0; word < 8; ++word)
        final_hash[word] = static_cast<uint32_t>(state[word / 2] >> ((word & 1) * 32));
    if (!meets_target(final_hash, target) || atomicCAS(&result->solution_found, 0, 1) != 0)
        return;

    result->nonce = start_nonce + index;
#pragma unroll
    for (int word = 0; word < 8; ++word)
    {
        reinterpret_cast<uint32_t*>(result->mix_hash)[word] = mix_hash[word];
        reinterpret_cast<uint32_t*>(result->final_hash)[word] = final_hash[word];
    }
}

// One eight-lane group evaluates one nonce. Each lane owns four consecutive
// mix words, turning every 128-byte DAG lookup into eight adjacent 16-byte
// reads instead of a single thread issuing all 32 scattered word loads.
[[maybe_unused]] __global__ void etchash_search_cooperative_kernel(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const uint32_t* header,
    const uint8_t* target,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    constexpr uint32_t group_width = 8;
    const auto global_lane = blockIdx.x * blockDim.x + threadIdx.x;
    const auto nonce_index = global_lane / group_width;
    if (nonce_index >= nonce_count)
        return;

    const auto group_lane = threadIdx.x & (group_width - 1);
    const auto warp_lane = threadIdx.x & 31u;
    const auto group_mask = 0xffu << (warp_lane & ~7u);
    const auto nonce = start_nonce + nonce_index;

    uint64_t seed_state[25]{};
    uint32_t seed[16]{};
    if (group_lane == 0)
    {
#pragma unroll
        for (int word = 0; word < 4; ++word)
            seed_state[word] = static_cast<uint64_t>(header[word * 2]) |
                (static_cast<uint64_t>(header[word * 2 + 1]) << 32);
        seed_state[4] = nonce;
        seed_state[5] = 0x0000000000000001ULL;
        seed_state[8] = 0x8000000000000000ULL;
        keccak_f1600(seed_state);
#pragma unroll
        for (int word = 0; word < 16; ++word)
            seed[word] = static_cast<uint32_t>(seed_state[word / 2] >> ((word & 1) * 32));
    }

    uint32_t broadcast_seed[16];
#pragma unroll
    for (int word = 0; word < 16; ++word)
        broadcast_seed[word] = __shfl_sync(group_mask, seed[word], 0, group_width);

    uint32_t mix[4];
#pragma unroll
    for (int word = 0; word < 4; ++word)
    {
        const auto seed_word = (group_lane * 4u + static_cast<uint32_t>(word)) & 15u;
        mix[word] = broadcast_seed[seed_word];
    }
    const auto seed_head = broadcast_seed[0];

#pragma unroll 1
    for (uint32_t access = 0; access < 64; ++access)
    {
        const auto selector = access & 31u;
        const auto selected_mix = __shfl_sync(
            group_mask, mix[selector & 3u], selector / 4u, group_width);
        const auto item = (((access ^ seed_head) * fnv_prime) ^ selected_mix) %
            full_dataset_items;
        const auto* data = dataset + static_cast<uint64_t>(item) * 32 + group_lane * 4u;
#pragma unroll
        for (int word = 0; word < 4; ++word)
            mix[word] = (mix[word] * fnv_prime) ^ data[word];
    }

    auto reduced = (mix[0] * fnv_prime) ^ mix[1];
    reduced = (reduced * fnv_prime) ^ mix[2];
    reduced = (reduced * fnv_prime) ^ mix[3];
    uint32_t gathered[8];
#pragma unroll
    for (int lane = 0; lane < 8; ++lane)
        gathered[lane] = __shfl_sync(group_mask, reduced, lane, group_width);

    if (group_lane != 0)
        return;

    uint64_t final_state[25]{};
#pragma unroll
    for (int word = 0; word < 8; ++word)
        final_state[word] = seed_state[word];
#pragma unroll
    for (int word = 0; word < 4; ++word)
        final_state[word + 8] = static_cast<uint64_t>(gathered[word * 2]) |
            (static_cast<uint64_t>(gathered[word * 2 + 1]) << 32);
    final_state[12] = 0x0000000000000001ULL;
    final_state[16] = 0x8000000000000000ULL;
    keccak_f1600(final_state);

    uint32_t final_hash[8];
#pragma unroll
    for (int word = 0; word < 8; ++word)
        final_hash[word] = static_cast<uint32_t>(final_state[word / 2] >> ((word & 1) * 32));
    if (!meets_target(final_hash, target) || atomicCAS(&result->solution_found, 0, 1) != 0)
        return;

    result->nonce = nonce;
#pragma unroll
    for (int word = 0; word < 8; ++word)
    {
        reinterpret_cast<uint32_t*>(result->mix_hash)[word] = gathered[word];
        reinterpret_cast<uint32_t*>(result->final_hash)[word] = final_hash[word];
    }
}

[[maybe_unused]] __global__ void search_kernel(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const int32_t block_number,
    const uint32_t* header,
    const uint8_t* target,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= nonce_count || result->solution_found != 0)
        return;

    uint32_t mix_hash[8];
    uint32_t final_hash[8];
    const auto nonce = start_nonce + index;
    kawpow_hash(dataset, full_dataset_items, block_number, header, nonce, mix_hash, final_hash);
    if (!meets_target(final_hash, target) || atomicCAS(&result->solution_found, 0, 1) != 0)
        return;

    result->nonce = nonce;
#pragma unroll
    for (int i = 0; i < 8; ++i)
    {
        reinterpret_cast<uint32_t*>(result->mix_hash)[i] = mix_hash[i];
        reinterpret_cast<uint32_t*>(result->final_hash)[i] = final_hash[i];
    }
}

[[maybe_unused]] __global__ void search_lane_kernel(
    const uint32_t* dataset,
    const uint32_t full_dataset_items,
    const int32_t block_number,
    const uint32_t* header,
    const uint8_t* target,
    const kawpow_program* program,
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    constexpr uint32_t groups_per_block = 8;
    __shared__ uint32_t initial_states[groups_per_block][8];
    __shared__ uint32_t reduced_mixes[groups_per_block][8];
    // Transposed so all lanes reading the same logical register hit separate
    // shared-memory banks. Dynamic ProgPoW register selection would otherwise
    // make nvcc spill the entire mix array to device-local memory.
    __shared__ uint32_t mixes[regs][128];

    const auto global_thread = blockIdx.x * blockDim.x + threadIdx.x;
    const auto nonce_index = global_thread / lanes;
    const auto lane = threadIdx.x & (lanes - 1);
    const auto group = threadIdx.x / lanes;
    const auto warp_lane = threadIdx.x & 31;
    const auto subgroup_mask = warp_lane < 16 ? 0x0000ffffu : 0xffff0000u;
    if (nonce_index >= nonce_count || result->solution_found != 0)
        return;

    constexpr uint32_t ravencoin[15] = {
        0x72, 0x41, 0x56, 0x45, 0x4e, 0x43, 0x4f, 0x49,
        0x4e, 0x4b, 0x41, 0x57, 0x50, 0x4f, 0x57};
    const auto nonce = start_nonce + nonce_index;
    if (lane == 0)
    {
        uint32_t state[25]{};
#pragma unroll
        for (int i = 0; i < 8; ++i)
            state[i] = header[i];
        state[8] = static_cast<uint32_t>(nonce);
        state[9] = static_cast<uint32_t>(nonce >> 32);
#pragma unroll
        for (int i = 10; i < 25; ++i)
            state[i] = ravencoin[i - 10];
        keccak_f800(state);
#pragma unroll
        for (int i = 0; i < 8; ++i)
            initial_states[group][i] = state[i];
    }
    __syncwarp(subgroup_mask);

    const auto z = fnv1a(fnv_offset, initial_states[group][0]);
    const auto w = fnv1a(z, initial_states[group][1]);
    const auto jsr = fnv1a(w, lane);
    const auto jcong = fnv1a(jsr, lane);
    kiss_state lane_rng{z, w, jsr, jcong};
#pragma unroll
    for (uint32_t reg = 0; reg < regs; ++reg)
        mixes[reg][threadIdx.x] = lane_rng.next();

    const auto dag_items_256 = full_dataset_items / 2;
    for (uint32_t round = 0; round < 64; ++round)
    {
        const auto item_index = __shfl_sync(
            subgroup_mask, mixes[0][threadIdx.x], static_cast<int>(round % lanes), lanes) % dag_items_256;
        const auto* item = dataset + static_cast<uint64_t>(item_index) * 64;

#pragma unroll
        for (uint32_t operation = 0; operation < 18; ++operation)
        {
            if (operation < 11)
            {
                const auto instruction = program->cache[operation];
                random_merge(
                    mixes[instruction.destination][threadIdx.x],
                    dataset[mixes[instruction.source][threadIdx.x] % l1_words],
                    instruction.selector);
            }
            const auto instruction = program->math[operation];
            const auto data = random_math(
                mixes[instruction.source1][threadIdx.x],
                mixes[instruction.source2][threadIdx.x],
                instruction.selector1);
            random_merge(
                mixes[instruction.destination][threadIdx.x], data, instruction.selector2);
        }

        const auto offset = ((lane ^ round) % lanes) * 4;
#pragma unroll
        for (uint32_t word = 0; word < 4; ++word)
            random_merge(
                mixes[program->dag_destinations[word]][threadIdx.x],
                item[offset + word],
                program->dag_selectors[word]);
    }

    auto lane_hash = fnv_offset;
#pragma unroll
    for (uint32_t reg = 0; reg < regs; ++reg)
        lane_hash = fnv1a(lane_hash, mixes[reg][threadIdx.x]);
    const auto paired_hash = __shfl_sync(subgroup_mask, lane_hash, (lane + 8) % lanes, lanes);
    if (lane < 8)
    {
        reduced_mixes[group][lane] = fnv1a(fnv1a(fnv_offset, lane_hash), paired_hash);
    }
    __syncwarp(subgroup_mask);

    if (lane != 0)
        return;

    uint32_t final_state[25]{};
#pragma unroll
    for (int i = 0; i < 8; ++i)
    {
        final_state[i] = initial_states[group][i];
        final_state[i + 8] = reduced_mixes[group][i];
    }
#pragma unroll
    for (int i = 16; i < 25; ++i)
        final_state[i] = ravencoin[i - 16];
    keccak_f800(final_state);
    if (!meets_target(final_state, target) || atomicCAS(&result->solution_found, 0, 1) != 0)
        return;

    result->nonce = nonce;
#pragma unroll
    for (int i = 0; i < 8; ++i)
    {
        reinterpret_cast<uint32_t*>(result->mix_hash)[i] = reduced_mixes[group][i];
        reinterpret_cast<uint32_t*>(result->final_hash)[i] = final_state[i];
    }
}

int32_t cuda_failure(const cudaError_t status)
{
    return status == cudaSuccess ? 0 : -2000 - static_cast<int32_t>(status);
}

cudaError_t ensure_search_buffers(trmadenci_cuda_epoch* epoch, const uint32_t nonce_count)
{
    auto status = cudaSuccess;
    if (epoch->search_header == nullptr)
        status = cudaMalloc(&epoch->search_header, 32);
    if (status == cudaSuccess && epoch->search_target == nullptr)
        status = cudaMalloc(&epoch->search_target, 32);
    if (status == cudaSuccess && epoch->search_result == nullptr)
        status = cudaMalloc(&epoch->search_result, sizeof(trmadenci_search_result));
    if (status != cudaSuccess || epoch->search_capacity >= nonce_count)
        return status;

    if (epoch->search_reduced != nullptr) cudaFree(epoch->search_reduced);
    if (epoch->search_initial != nullptr) cudaFree(epoch->search_initial);
    epoch->search_reduced = nullptr;
    epoch->search_initial = nullptr;
    epoch->search_capacity = 0;
    const auto initial_bytes = static_cast<size_t>(nonce_count) * 16 * sizeof(uint32_t);
    const auto reduced_bytes = static_cast<size_t>(nonce_count) * 8 * sizeof(uint32_t);
    status = cudaMalloc(&epoch->search_initial, initial_bytes);
    if (status == cudaSuccess)
        status = cudaMalloc(&epoch->search_reduced, reduced_bytes);
    if (status == cudaSuccess)
        epoch->search_capacity = nonce_count;
    return status;
}
}

int32_t trmadenci_search_cuda(
    trmadenci_cuda_epoch* epoch,
    const int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    if (epoch == nullptr || epoch->algorithm_kind != 1 || block_number < 0 ||
        header_hash == nullptr || target == nullptr ||
        nonce_count == 0 || result == nullptr || block_number / 7500 != epoch->epoch_number)
        return -1;

    auto status = cudaSetDevice(epoch->device_index);
    if (status == cudaSuccess)
        status = ensure_search_buffers(epoch, nonce_count);
    auto* device_header = epoch->search_header;
    auto* device_target = epoch->search_target;
    auto* device_initial = epoch->search_initial;
    auto* device_reduced = epoch->search_reduced;
    auto* device_result = static_cast<trmadenci_search_result*>(epoch->search_result);
    if (status == cudaSuccess)
        status = cudaMemcpy(device_header, header_hash, 32, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
        status = cudaMemcpy(device_target, target, 32, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
        status = cudaMemset(device_result, 0, sizeof(trmadenci_search_result));

    const auto jit_status = status == cudaSuccess ? ensure_jit_program(epoch, block_number) : 0;
    if (jit_status != 0)
        return jit_status;

    const auto started = std::chrono::steady_clock::now();
    if (status == cudaSuccess)
    {
        constexpr uint32_t threads = 128;
        const auto scalar_blocks = (nonce_count + threads - 1) / threads;
        const auto mix_blocks = (static_cast<uint64_t>(nonce_count) * lanes + threads - 1) / threads;
        auto* dataset = epoch->dataset;
        auto full_dataset_items = epoch->full_dataset_items;
        auto* kernel_header = device_header;
        auto* kernel_target = device_target;
        auto* kernel_initial = device_initial;
        auto* kernel_reduced = device_reduced;
        auto kernel_start_nonce = start_nonce;
        auto kernel_nonce_count = nonce_count;
        auto* kernel_result = device_result;
        void* seed_arguments[] = {
            &kernel_header, &kernel_start_nonce, &kernel_nonce_count, &kernel_initial};
        auto launch_status = cuLaunchKernel(
            reinterpret_cast<CUfunction>(epoch->jit_seed_function),
            scalar_blocks, 1, 1,
            threads, 1, 1,
            0, nullptr, seed_arguments, nullptr);
        void* mix_arguments[] = {
            &dataset, &full_dataset_items, &kernel_initial, &kernel_nonce_count, &kernel_reduced};
        if (launch_status == CUDA_SUCCESS)
            launch_status = cuLaunchKernel(
                reinterpret_cast<CUfunction>(epoch->jit_mix_function),
                static_cast<uint32_t>(mix_blocks), 1, 1,
                threads, 1, 1,
                0, nullptr, mix_arguments, nullptr);
        void* final_arguments[] = {
            &kernel_initial, &kernel_reduced, &kernel_target,
            &kernel_start_nonce, &kernel_nonce_count, &kernel_result};
        if (launch_status == CUDA_SUCCESS)
            launch_status = cuLaunchKernel(
                reinterpret_cast<CUfunction>(epoch->jit_final_function),
                scalar_blocks, 1, 1,
                threads, 1, 1,
                0, nullptr, final_arguments, nullptr);
        if (launch_status != CUDA_SUCCESS)
            return -4000 - static_cast<int32_t>(launch_status);
    }
    if (status == cudaSuccess)
        status = cudaDeviceSynchronize();
    const auto finished = std::chrono::steady_clock::now();

    *result = {};
    if (status == cudaSuccess)
        status = cudaMemcpy(result, device_result, sizeof(*result), cudaMemcpyDeviceToHost);
    if (status != cudaSuccess)
        return cuda_failure(status);

    result->hashes_searched = nonce_count;
    result->search_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    return 0;
}

int32_t trmadenci_search_etchash_cuda(
    trmadenci_cuda_epoch* epoch,
    const int32_t block_number,
    const uint8_t header_hash[32],
    const uint8_t target[32],
    const uint64_t start_nonce,
    const uint32_t nonce_count,
    trmadenci_search_result* result)
{
    constexpr int32_t activation_block = 11'700'000;
    const auto epoch_length = block_number < activation_block ? 30'000 : 60'000;
    if (epoch == nullptr || epoch->algorithm_kind != 2 || block_number < 0 ||
        header_hash == nullptr || target == nullptr || nonce_count == 0 || result == nullptr ||
        block_number / epoch_length != epoch->epoch_number)
        return -1;

    auto status = cudaSetDevice(epoch->device_index);
    if (status == cudaSuccess)
        status = ensure_search_buffers(epoch, nonce_count);
    auto* device_result = static_cast<trmadenci_search_result*>(epoch->search_result);
    if (status == cudaSuccess)
        status = cudaMemcpy(epoch->search_header, header_hash, 32, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
        status = cudaMemcpy(epoch->search_target, target, 32, cudaMemcpyHostToDevice);
    if (status == cudaSuccess)
        status = cudaMemset(device_result, 0, sizeof(trmadenci_search_result));

    const auto started = std::chrono::steady_clock::now();
    if (status == cudaSuccess)
    {
        constexpr uint32_t scalar_threads = 256;
        constexpr uint32_t mix_threads = 32;
        const auto scalar_blocks = (nonce_count + scalar_threads - 1) / scalar_threads;
        const auto mix_blocks = (nonce_count + mix_threads - 1) / mix_threads;
        etchash_seed_kernel<<<scalar_blocks, scalar_threads>>>(
            epoch->search_header,
            start_nonce,
            nonce_count,
            epoch->search_initial);
        status = cudaGetLastError();
        if (status == cudaSuccess)
            etchash_mix_kernel<<<mix_blocks, mix_threads>>>(
            epoch->dataset,
            epoch->full_dataset_items,
            epoch->search_initial,
            nonce_count,
            epoch->search_reduced);
        if (status == cudaSuccess)
            status = cudaGetLastError();
        if (status == cudaSuccess)
            etchash_final_kernel<<<scalar_blocks, scalar_threads>>>(
            epoch->search_initial,
            epoch->search_reduced,
            epoch->search_target,
            start_nonce,
            nonce_count,
            device_result);
        if (status == cudaSuccess)
            status = cudaGetLastError();
    }
    if (status == cudaSuccess)
        status = cudaDeviceSynchronize();
    const auto finished = std::chrono::steady_clock::now();

    *result = {};
    if (status == cudaSuccess)
        status = cudaMemcpy(result, device_result, sizeof(*result), cudaMemcpyDeviceToHost);
    if (status != cudaSuccess)
        return cuda_failure(status);
    result->hashes_searched = nonce_count;
    result->search_milliseconds =
        std::chrono::duration<double, std::milli>(finished - started).count();
    return 0;
}
