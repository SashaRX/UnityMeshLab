cmake_minimum_required(VERSION 3.20)
project(xatlas-unity-bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ── Fetch xatlas source ──
include(FetchContent)
FetchContent_Declare(
    xatlas
    GIT_REPOSITORY https://github.com/jpcy/xatlas.git
    GIT_TAG        main
    GIT_SHALLOW    TRUE
)
FetchContent_Declare(
    meshoptimizer
    GIT_REPOSITORY https://github.com/zeux/meshoptimizer.git
    GIT_TAG        v0.22
    GIT_SHALLOW    TRUE
)
set(MESHOPT_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
# meshoptimizer static lib must be PIC when linked into our shared lib (Linux/GCC)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
FetchContent_MakeAvailable(xatlas meshoptimizer)

# ── Build shared library ──
add_library(xatlas-unity SHARED
    xatlas-unity-bridge.cpp
    ${xatlas_SOURCE_DIR}/source/xatlas/xatlas.cpp
)

target_include_directories(xatlas-unity PRIVATE
    ${xatlas_SOURCE_DIR}/source/xatlas
)

# Link meshoptimizer (static lib baked into our shared lib)
target_link_libraries(xatlas-unity PRIVATE meshoptimizer)

# Release optimizations
target_compile_options(xatlas-unity PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/O2 /DNDEBUG>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-O2 -DNDEBUG>
)

# Output name without lib prefix
set_target_properties(xatlas-unity PROPERTIES
    OUTPUT_NAME "xatlas-unity"
    PREFIX ""
)

# ── Post-build: copy DLL to Unity Plugins ──
set(UNITY_PLUGINS_DIR "${CMAKE_SOURCE_DIR}/../Plugins/x86_64")

add_custom_command(TARGET xatlas-unity POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGINS_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:xatlas-unity>"
        "${UNITY_PLUGINS_DIR}/$<TARGET_FILE_NAME:xatlas-unity>"
    COMMENT "Copying DLL to ${UNITY_PLUGINS_DIR}"
)
