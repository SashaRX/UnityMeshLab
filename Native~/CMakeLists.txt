cmake_minimum_required(VERSION 3.20)
project(xatlas-unity-bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ── meshoptimizer (still fetched — we don't patch it) ──
include(FetchContent)
FetchContent_Declare(
    meshoptimizer
    GIT_REPOSITORY https://github.com/zeux/meshoptimizer.git
    GIT_TAG        v1.1
    GIT_SHALLOW    TRUE
)
set(MESHOPT_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
# meshoptimizer static lib must be PIC when linked into our shared lib (Linux/GCC)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
FetchContent_MakeAvailable(meshoptimizer)

# ── xatlas: vendored fork with PreserveChartScale patch ──
# See Native~/third_party/xatlas/xatlas.cpp around the "Scale the charts to
# use the entire texel area" comment for the patch. Required because stock
# xatlas unconditionally rescales each chart to its ceil(extents) integer
# bbox, which destroys uniform per-shell lightmap texel density. Upstream
# Issue #18 (closed wontfix) tracks the same problem.
set(XATLAS_SOURCE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/third_party/xatlas)

# ── Build shared library ──
add_library(xatlas-unity SHARED
    xatlas-unity-bridge.cpp
    src/collision.cpp
    ${XATLAS_SOURCE_DIR}/xatlas.cpp
)

target_include_directories(xatlas-unity PRIVATE
    ${XATLAS_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party
)

# Link meshoptimizer (static lib baked into our shared lib)
target_link_libraries(xatlas-unity PRIVATE meshoptimizer)

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

add_custom_command(TARGET xatlas-unity POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGINS_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:xatlas-unity>"
        "${UNITY_PLUGINS_DIR}/$<TARGET_FILE_NAME:xatlas-unity>"
    COMMENT "Copying library to ${UNITY_PLUGINS_DIR}"
)
