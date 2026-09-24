cmake_minimum_required(VERSION 3.16)
project(oscarwatch_ft8 C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(FT8_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../ft8_lib")

set(FT8_SOURCES
  ${FT8_ROOT}/ft8/constants.c
  ${FT8_ROOT}/ft8/crc.c
  ${FT8_ROOT}/ft8/decode.c
  ${FT8_ROOT}/ft8/encode.c
  ${FT8_ROOT}/ft8/ldpc.c
  ${FT8_ROOT}/ft8/message.c
  ${FT8_ROOT}/ft8/text.c
  ${FT8_ROOT}/common/monitor.c
  ${FT8_ROOT}/fft/kiss_fft.c
  ${FT8_ROOT}/fft/kiss_fftr.c
  oscarwatch_ft8.c
)

add_library(oscarwatch_ft8 SHARED ${FT8_SOURCES})
target_include_directories(oscarwatch_ft8 PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}
  ${FT8_ROOT}
  ${FT8_ROOT}/ft8
  ${FT8_ROOT}/common
  ${FT8_ROOT}/fft
)
target_compile_definitions(oscarwatch_ft8 PRIVATE OSCARWATCH_FT8_EXPORTS)
if(UNIX)
  target_link_libraries(oscarwatch_ft8 PRIVATE m pthread)
endif()

if(MSVC)
  target_compile_options(oscarwatch_ft8 PRIVATE /O2)
  target_link_options(oscarwatch_ft8 PRIVATE /LTCG)
else()
  # Always optimise the shipped native decode library (WSL MinGW and CI Release builds).
  target_compile_options(oscarwatch_ft8 PRIVATE -O3 -flto)
  target_link_options(oscarwatch_ft8 PRIVATE -flto)
  # __thread (monitor cache) needs libgcc emutls; static-link so operators need no extra DLL.
  if(CMAKE_C_COMPILER MATCHES "mingw" OR WIN32)
    target_link_options(oscarwatch_ft8 PRIVATE -static-libgcc)
  endif()
endif()

# Output name without lib prefix quirks for P/Invoke discovery
set_target_properties(oscarwatch_ft8 PROPERTIES
  OUTPUT_NAME "oscarwatch_ft8"
  PREFIX ""
)

install(TARGETS oscarwatch_ft8 RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
