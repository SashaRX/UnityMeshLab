cmake_minimum_required(VERSION 3.20)
project(xatlas-unity-bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Optional ISPC mask experiment; generated dispatch supports SSE2 and AVX2.
# No published plugin uses this backend until paired model results justify it.
option(MESHLAB_ISPC_PACKING "Use ISPC integer SIMD for large packing masks" OFF)
if(MESHLAB_ISPC_PACKING)
    find_program(MESHLAB_ISPC_EXECUTABLE NAMES ispc REQUIRED)
    set(MESHLAB_ISPC_OBJECTS "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks_sse2.obj" "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks_avx2.obj")
    if(NOT WIN32)
        message(FATAL_ERROR "Experimental ISPC benchmark currently supports Windows only")
    endif()
    add_custom_command(OUTPUT ${MESHLAB_ISPC_OBJECTS} "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks.h"
        COMMAND "${MESHLAB_ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/src/ispc_masks.ispc"
            --arch=x86-64 --target=sse2-i32x4,avx2-i32x8 -O2
            -h "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks.h" -o "${CMAKE_CURRENT_BINARY_DIR}/ispc_masks.obj"
        DEPENDS src/ispc_masks.ispc VERBATIM)
    add_library(meshlab-ispc-masks STATIC ${MESHLAB_ISPC_OBJECTS})
    set_target_properties(meshlab-ispc-masks PROPERTIES LINKER_LANGUAGE CXX)
    target_include_directories(meshlab-ispc-masks INTERFACE "${CMAKE_CURRENT_BINARY_DIR}")
    target_compile_definitions(meshlab-ispc-masks INTERFACE MESHLAB_ISPC_PACKING=1)
endif()

# ── Dependencies ──
include(FetchContent)
FetchContent_Declare(
    meshoptimizer
    GIT_REPOSITORY https://github.com/zeux/meshoptimizer.git
    GIT_TAG        9e1f07b159d3cb777f1c67ed31fc11fd117986f4 # v1.3 (2026-09-25)
)
set(MESHOPT_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
# meshoptimizer static lib must be PIC when linked into our shared lib (Linux/GCC)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
FetchContent_MakeAvailable(meshoptimizer)

# Optional Intel oneTBB experiment. Keep normal plugin builds dependency-free
# until the paired model benchmark establishes a benefit. Pin reviewed sources.
option(MESHLAB_TBB_PACKING "Use oneTBB for deterministic chart placement searches" OFF)
if(MESHLAB_TBB_PACKING)
    FetchContent_Declare(meshlab_tbb
        GIT_REPOSITORY https://github.com/uxlfoundation/oneTBB.git
        GIT_TAG 9afd759b72c0c233cd5ea3c3c06b0894c9da9c54 # v2021.12.0
    )
    set(TBB_TEST OFF CACHE BOOL "" FORCE)
    set(TBBMALLOC_BUILD OFF CACHE BOOL "" FORCE)
    set(TBB_DISABLE_HWLOC_AUTOMATIC_SEARCH ON CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(meshlab_tbb)
endif()

# Only the remesher needs a local, reviewed extension: upstream stores row offsets
# in one byte and supports at most 256 cells. Preserve its byte path for existing
# jobs and use 16-bit offsets above that. Keep the rest of meshoptimizer pinned.
get_target_property(MESHOPT_SOURCES meshoptimizer SOURCES)
set(MESHOPT_FILTERED_SOURCES "")
set(MESHOPT_REMESHER_FOUND OFF)
foreach(MESHOPT_SOURCE IN LISTS MESHOPT_SOURCES)
    if(MESHOPT_SOURCE MATCHES "(^|/)remesher\\.cpp$")
        set(MESHOPT_REMESHER_FOUND ON)
    else()
        get_filename_component(MESHOPT_SOURCE_ABSOLUTE "${MESHOPT_SOURCE}"
            ABSOLUTE BASE_DIR "${meshoptimizer_SOURCE_DIR}")
        list(APPEND MESHOPT_FILTERED_SOURCES "${MESHOPT_SOURCE_ABSOLUTE}")
    endif()
endforeach()
if(NOT MESHOPT_REMESHER_FOUND)
    message(FATAL_ERROR "Pinned meshoptimizer remesher source was not found")
endif()
set_property(TARGET meshoptimizer PROPERTY SOURCES "${MESHOPT_FILTERED_SOURCES}")
target_sources(meshoptimizer PRIVATE third_party/meshoptimizer/remesher.cpp)
target_include_directories(meshoptimizer PRIVATE "${meshoptimizer_SOURCE_DIR}/src")

# Keep xatlas vendored (reviewed extension: third_party/xatlas/MESHLAB_PATCHES.md)
# so native builds never execute or compile code from a
# moving upstream branch. Update this reviewed snapshot explicitly when needed.
set(XATLAS_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/xatlas")

# ── Build shared library ──
add_library(xatlas-unity SHARED
    xatlas-unity-bridge.cpp
    src/collision.cpp
    src/remesh.cpp
    src/footprint_search.cpp
)

target_include_directories(xatlas-unity PRIVATE
    ${XATLAS_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party
)

# Link meshoptimizer (static lib baked into our shared lib)
target_link_libraries(xatlas-unity PRIVATE meshoptimizer)
if(MESHLAB_ISPC_PACKING)
    target_link_libraries(xatlas-unity PRIVATE meshlab-ispc-masks)
endif()
if(MESHLAB_TBB_PACKING)
    target_link_libraries(xatlas-unity PRIVATE TBB::tbb)
    target_compile_definitions(xatlas-unity PRIVATE MESHLAB_TBB_PACKING=1)
    if(WIN32)
        add_custom_command(TARGET xatlas-unity POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:tbb>" "$<TARGET_FILE_DIR:xatlas-unity>"
        )
    endif()
endif()

# Release optimizations
target_compile_options(xatlas-unity PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/O2 /DNDEBUG>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-O2 -DNDEBUG>
)

# Output name — keep "xatlas-unity" on all platforms.
# On Unix the default "lib" prefix is kept so Unity can find "xatlas-unity" via DllImport.
set_target_properties(xatlas-unity PROPERTIES
    OUTPUT_NAME "xatlas-unity"
)
if(WIN32)
    set_target_properties(xatlas-unity PROPERTIES PREFIX "")
endif()

# ── Post-build: copy library to Unity Plugins ──
if(APPLE)
    set(UNITY_PLUGINS_DIR "${CMAKE_SOURCE_DIR}/../Plugins/macOS")
else()
    set(UNITY_PLUGINS_DIR "${CMAKE_SOURCE_DIR}/../Plugins/x86_64")
endif()

option(MESHLAB_COPY_PLUGINS "Copy built libraries into package Plugins (CI publication)" ON)
option(MESHLAB_EMBREE_QUERIES "Build optional compatible Embree discovery backend" OFF)
set(MESHLAB_EMBREE_ROOT "" CACHE PATH "Pinned Embree 4.3.3 CPU distribution")
if(MESHLAB_EMBREE_QUERIES)
    if(MESHLAB_COPY_PLUGINS)
        message(FATAL_ERROR "Embree runtime/license publication is not integrated; use MESHLAB_COPY_PLUGINS=OFF")
    endif()
    find_package(embree 4.3.3 EXACT CONFIG REQUIRED PATHS "${MESHLAB_EMBREE_ROOT}" NO_DEFAULT_PATH)
    add_library(meshlab-queries SHARED src/embree_queries.cpp)
    target_link_libraries(meshlab-queries PRIVATE embree)
    target_compile_options(meshlab-queries PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/O2 /fp:strict>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-O2 -ffp-contract=off>)
    if(WIN32)
        foreach(runtime embree4 tbb12 tbbmalloc)
            add_custom_command(TARGET meshlab-queries POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${MESHLAB_EMBREE_ROOT}/bin/${runtime}.dll" "$<TARGET_FILE_DIR:meshlab-queries>")
        endforeach()
    endif()
endif()
if(MESHLAB_ISPC_PACKING AND MESHLAB_COPY_PLUGINS)
    message(FATAL_ERROR "Experimental ISPC builds require MESHLAB_COPY_PLUGINS=OFF")
endif()
if(MESHLAB_TBB_PACKING AND MESHLAB_COPY_PLUGINS)
    message(FATAL_ERROR "Experimental oneTBB builds require MESHLAB_COPY_PLUGINS=OFF; runtime publication is not integrated")
endif()
if(MESHLAB_COPY_PLUGINS)
add_custom_command(TARGET xatlas-unity POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGINS_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:xatlas-unity>"
        "${UNITY_PLUGINS_DIR}/$<TARGET_FILE_NAME:xatlas-unity>"
    COMMENT "Copying library to ${UNITY_PLUGINS_DIR}"
)
endif()

# Opt-in headless tests; no Unity license needed.
option(MESHLAB_BUILD_TESTS "Build native regression tests" OFF)
if(MESHLAB_BUILD_TESTS)
    enable_testing()
    add_executable(meshlab-remesh-test tests/remesh_test.cpp)
    target_link_libraries(meshlab-remesh-test PRIVATE xatlas-unity)
    add_test(NAME remesh COMMAND meshlab-remesh-test)
    add_executable(meshlab-uv-input-test tests/uv_input_test.cpp)
    target_link_libraries(meshlab-uv-input-test PRIVATE xatlas-unity)
    add_test(NAME uv-input COMMAND meshlab-uv-input-test)
    add_executable(meshlab-crease-test tests/crease_test.cpp)
    target_link_libraries(meshlab-crease-test PRIVATE xatlas-unity)
    add_test(NAME crease COMMAND meshlab-crease-test)
    add_executable(meshlab-bit-image-test tests/bit_image_test.cpp)
    if(MESHLAB_ISPC_PACKING)
        target_link_libraries(meshlab-bit-image-test PRIVATE meshlab-ispc-masks)
    endif()
    add_test(NAME bit-image COMMAND meshlab-bit-image-test)
    add_executable(meshlab-footprint-search-test tests/footprint_search_test.cpp)
    target_link_libraries(meshlab-footprint-search-test PRIVATE xatlas-unity)
    add_test(NAME footprint-search COMMAND meshlab-footprint-search-test)
    if(MESHLAB_TBB_PACKING)
        add_executable(meshlab-parallel-packing-test tests/parallel_packing_test.cpp)
        target_compile_definitions(meshlab-parallel-packing-test PRIVATE MESHLAB_TBB_PACKING=1)
        target_link_libraries(meshlab-parallel-packing-test PRIVATE TBB::tbb)
        add_test(NAME parallel-packing COMMAND meshlab-parallel-packing-test)
    endif()
    add_executable(meshlab-packing-replay tests/packing_replay.cpp)
    target_link_libraries(meshlab-packing-replay PRIVATE xatlas-unity)
endif()
