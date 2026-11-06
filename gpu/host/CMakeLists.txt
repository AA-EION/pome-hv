# Pome Virtual GPU host companion (pvg-host). SPDX-License-Identifier: MIT
cmake_minimum_required(VERSION 3.24)
project(pvg_host LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Vulkan headers only (Khronos, Apache-2.0/MIT). The loader is the system vulkan-1.dll.
set(PVG_VULKAN_HEADERS "" CACHE PATH "Directory containing vulkan/vulkan.h")
if(NOT PVG_VULKAN_HEADERS)
  include(FetchContent)
  FetchContent_Declare(vulkan_headers GIT_REPOSITORY https://github.com/KhronosGroup/Vulkan-Headers.git GIT_TAG 9fbc2a7 GIT_SHALLOW FALSE)
  FetchContent_MakeAvailable(vulkan_headers)
  set(PVG_VULKAN_HEADERS ${vulkan_headers_SOURCE_DIR}/include)
endif()

# Venus comes from libvirglrenderer-1.dll (MinGW-w64 build, gpu/host/virglrenderer/build-windows.ps1),
# loaded at run time from pvg-host's directory. Without it pvg-host still negotiates capabilities.

add_library(pvg_core STATIC src/caps.cpp src/transport.cpp src/session.cpp src/renderer_null.cpp src/renderer_venus.cpp)
target_include_directories(pvg_core PUBLIC src ${CMAKE_CURRENT_SOURCE_DIR}/../protocol ${PVG_VULKAN_HEADERS})
if(MSVC)
  target_compile_options(pvg_core PUBLIC /W4 /permissive- /utf-8)
  target_compile_definitions(pvg_core PUBLIC NOMINMAX _CRT_SECURE_NO_WARNINGS)
endif()

add_executable(pvg-host src/main.cpp)
target_link_libraries(pvg-host PRIVATE pvg_core)

add_executable(pvg-host-tests tests/host_tests.cpp)
target_link_libraries(pvg-host-tests PRIVATE pvg_core)
enable_testing()
add_test(NAME pvg-host-tests COMMAND pvg-host-tests)
