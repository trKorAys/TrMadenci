cmake_minimum_required(VERSION 3.28)

project(TrMadenciNative VERSION 0.1.0 LANGUAGES C CXX CUDA)
find_package(CUDAToolkit REQUIRED)

set(KAWPOW_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/cpp-kawpow)
set(RANDOMX_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/RandomX)

add_subdirectory(${RANDOMX_ROOT} ${CMAKE_CURRENT_BINARY_DIR}/randomx EXCLUDE_FROM_ALL)

add_library(kawpow-keccak STATIC
    ${KAWPOW_ROOT}/lib/keccak/keccak.c
    ${KAWPOW_ROOT}/lib/keccak/keccakf800.c
    ${KAWPOW_ROOT}/lib/keccak/keccakf1600.c
)
target_include_directories(kawpow-keccak PUBLIC ${KAWPOW_ROOT}/include)

add_library(kawpow-reference STATIC
    ${KAWPOW_ROOT}/lib/ethash/ethash.cpp
    ${KAWPOW_ROOT}/lib/ethash/managed.cpp
    ${KAWPOW_ROOT}/lib/ethash/primes.c
    ${KAWPOW_ROOT}/lib/ethash/progpow.cpp
)
target_include_directories(kawpow-reference
    PUBLIC ${KAWPOW_ROOT}/include
    PRIVATE ${KAWPOW_ROOT}/lib
)
target_link_libraries(kawpow-reference PRIVATE kawpow-keccak)
target_compile_features(kawpow-reference PRIVATE cxx_std_17)

if(MSVC)
    set_target_properties(kawpow-keccak kawpow-reference PROPERTIES
        MSVC_RUNTIME_LIBRARY "MultiThreaded"
    )
endif()

add_library(TrMadenci.Native SHARED
    include/trmadenci_native.h
    src/dag_generator.cu
    src/device_probe.cu
    src/gpu_telemetry.cpp
    src/nonce_search.cu
    src/octopus_reference.cpp
    src/opencl_probe.cpp
    src/reference_bridge.cpp
    src/randomx_bridge.cpp
)

target_compile_definitions(TrMadenci.Native PRIVATE TRMADENCI_NATIVE_EXPORTS)
target_include_directories(TrMadenci.Native PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_include_directories(TrMadenci.Native PRIVATE
    ${KAWPOW_ROOT}/lib/ethash
    ${RANDOMX_ROOT}/src
)
target_compile_features(TrMadenci.Native PRIVATE cxx_std_20)
target_link_libraries(TrMadenci.Native PRIVATE
    kawpow-reference
    randomx
    CUDA::cuda_driver
    CUDA::nvrtc
    CUDA::nvml
)

set_target_properties(TrMadenci.Native PROPERTIES
    CUDA_ARCHITECTURES "75;86;89;120"
    CUDA_STANDARD 17
    CUDA_STANDARD_REQUIRED YES
    MSVC_RUNTIME_LIBRARY "MultiThreaded"
    OUTPUT_NAME "TrMadenci.Native"
)

if(MSVC)
    set_target_properties(randomx PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
    target_compile_options(randomx PRIVATE
        $<$<COMPILE_LANGUAGE:C>:/wd4067>
        $<$<COMPILE_LANGUAGE:C>:/wd4804>
        $<$<COMPILE_LANGUAGE:CXX>:/wd4067>
        $<$<COMPILE_LANGUAGE:CXX>:/wd4804>
    )
    target_compile_options(TrMadenci.Native PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4 /WX>)
    target_compile_options(TrMadenci.Native PRIVATE $<$<COMPILE_LANGUAGE:CUDA>:-Xcompiler=/wd4211>)
endif()

include(CTest)
if(BUILD_TESTING)
    add_executable(TrMadenci.Native.Tests tests/device_probe_tests.cpp)
    target_link_libraries(TrMadenci.Native.Tests PRIVATE TrMadenci.Native kawpow-reference)
    target_include_directories(TrMadenci.Native.Tests PRIVATE ${KAWPOW_ROOT}/test/unittests)
    target_compile_features(TrMadenci.Native.Tests PRIVATE cxx_std_20)
    set_target_properties(TrMadenci.Native.Tests PROPERTIES
        MSVC_RUNTIME_LIBRARY "MultiThreaded"
    )
    add_test(NAME device_probe COMMAND TrMadenci.Native.Tests)
    get_filename_component(TRMADENCI_CUDA_BIN_DIR "${CMAKE_CUDA_COMPILER}" DIRECTORY)
    set(TRMADENCI_CUDA_RUNTIME_DIR "${TRMADENCI_CUDA_BIN_DIR}/x64")
    add_custom_command(TARGET TrMadenci.Native.Tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${TRMADENCI_CUDA_RUNTIME_DIR}/nvrtc64_130_0.dll"
            "$<TARGET_FILE_DIR:TrMadenci.Native.Tests>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${TRMADENCI_CUDA_RUNTIME_DIR}/nvrtc-builtins64_131.dll"
            "$<TARGET_FILE_DIR:TrMadenci.Native.Tests>"
    )
    set_tests_properties(device_probe PROPERTIES
        ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:${TRMADENCI_CUDA_RUNTIME_DIR}"
    )
endif()
