cmake_minimum_required(VERSION 3.16)

# Apple Silicon CI runners cross-build osx-x64 with Homebrew arm64 GCC; arch flags
# must be set before project() so Fortran compiler detection matches CMAKE_OSX_ARCHITECTURES.
if(APPLE AND CMAKE_OSX_ARCHITECTURES)
  set(_ft4_osx_arch_flags "")
  foreach(_arch IN LISTS CMAKE_OSX_ARCHITECTURES)
    set(_ft4_osx_arch_flags "${_ft4_osx_arch_flags} -arch ${_arch}")
  endforeach()
  set(CMAKE_C_FLAGS_INIT "${_ft4_osx_arch_flags}")
  set(CMAKE_CXX_FLAGS_INIT "${_ft4_osx_arch_flags}")
  set(CMAKE_Fortran_FLAGS_INIT "${_ft4_osx_arch_flags}")
endif()

project(ft4_coder C CXX Fortran)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

include(FetchContent)
set(WSJTX_LIB_GIT_TAG "8f182a126620459314eb26a3647cb7e542cd7d62" CACHE STRING "wsjtx_lib commit (full git SHA, not a blob id)")
FetchContent_Declare(
  wsjtx_lib
  GIT_REPOSITORY https://github.com/paulh002/wsjtx_lib.git
  GIT_TAG ${WSJTX_LIB_GIT_TAG}
)
FetchContent_GetProperties(wsjtx_lib)
if(NOT wsjtx_lib_POPULATED)
  FetchContent_Populate(wsjtx_lib)
endif()
set(WSJTX_LIB_DIR ${wsjtx_lib_SOURCE_DIR})

add_definitions(-DBIGSYM=1 -DFFTW_ENABLE_FLOAT -DCMAKE_BUILD)

set(CMAKE_Fortran_FLAGS "${CMAKE_Fortran_FLAGS} -fno-f2c")
if(CMAKE_Fortran_COMPILER_ID STREQUAL "GNU")
  set(CMAKE_Fortran_FLAGS "${CMAKE_Fortran_FLAGS} -Wno-unused-dummy-argument")
endif()

include(${CMAKE_CURRENT_SOURCE_DIR}/cmake/WsjtxLibSources.cmake)

set(WSJTX_FORTRAN)
foreach(rel IN LISTS WSJTX_FORTRAN_REL)
  list(APPEND WSJTX_FORTRAN "${WSJTX_LIB_DIR}/${rel}")
endforeach()

set(WSJTX_C)
foreach(rel IN LISTS WSJTX_C_REL)
  list(APPEND WSJTX_C "${WSJTX_LIB_DIR}/${rel}")
endforeach()

set(WSJTX_CPP)
foreach(rel IN LISTS WSJTX_CPP_REL)
  list(APPEND WSJTX_CPP "${WSJTX_LIB_DIR}/${rel}")
endforeach()

set_source_files_properties("${WSJTX_LIB_DIR}/lib/decoder.f90" PROPERTIES
  COMPILE_FLAGS "-Wno-unused-dummy-argument"
)

list(LENGTH WSJTX_FORTRAN _wsjtx_fortran_count)
list(LENGTH WSJTX_C _wsjtx_c_count)
message(STATUS "ft4_coder: ${_wsjtx_fortran_count} Fortran + ${_wsjtx_c_count} C sources (curated wsjtx_lib list)")
if(_wsjtx_fortran_count GREATER 246 OR _wsjtx_c_count GREATER 26)
  message(FATAL_ERROR "Unexpected wsjtx source count; expected curated cmake/WsjtxLibSources.cmake")
endif()

add_library(ft4_coder SHARED
  src/ft4_coder_shim.c
  src/wsjtx_decode_stubs.c
  ${WSJTX_FORTRAN}
  ${WSJTX_C}
  ${WSJTX_CPP}
)

# gfortran must not drive the shared-library link: it omits C/C++/FFTW objects.
set_target_properties(ft4_coder PROPERTIES LINKER_LANGUAGE CXX)

target_compile_definitions(ft4_coder PRIVATE FT4_CODER_BUILD)
if(NOT WIN32)
  set_target_properties(ft4_coder PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
  )
endif()

find_path(BOOST_INCLUDE_DIR NAMES boost/crc.hpp
  HINTS
    $ENV{MINGW_PREFIX}/include
    /usr/include
    /usr/local/include
    /opt/homebrew/include
    /usr/local/opt/boost/include
)
if(NOT BOOST_INCLUDE_DIR)
  message(FATAL_ERROR "boost/crc.hpp not found; install Boost headers (e.g. libboost-dev, mingw-w64-x86_64-boost)")
endif()

target_include_directories(ft4_coder
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${WSJTX_LIB_DIR}
    ${WSJTX_LIB_DIR}/lib
    ${BOOST_INCLUDE_DIR}
)

if(APPLE)
  set_target_properties(ft4_coder PROPERTIES
    OUTPUT_NAME "ft4_coder"
    BUILD_WITH_INSTALL_RPATH TRUE
    INSTALL_RPATH "@loader_path"
  )
elseif(WIN32)
  set_target_properties(ft4_coder PROPERTIES
    OUTPUT_NAME "ft4_coder"
    PREFIX ""
  )
else()
  set_target_properties(ft4_coder PROPERTIES OUTPUT_NAME "ft4_coder")
endif()

find_package(Threads REQUIRED)

set(FFTW3_PREFIX "" CACHE PATH "Optional FFTW install prefix (Homebrew: brew --prefix fftw)")
set(FFTW3F_LIB "" CACHE FILEPATH "Path to libfftw3f (optional; used for osx-x64 cross-build CI)")
set(FFTW3F_THREADS_LIB "" CACHE FILEPATH "Path to libfftw3f_threads (optional)")

set(_ft4_fftw_hints)
if(FFTW3_PREFIX)
  list(APPEND _ft4_fftw_hints "${FFTW3_PREFIX}")
endif()
if(FFTW3F_LIB)
  get_filename_component(_ft4_fftw_lib_dir "${FFTW3F_LIB}" DIRECTORY)
  get_filename_component(_ft4_fftw_prefix_from_lib "${_ft4_fftw_lib_dir}" DIRECTORY)
  list(APPEND _ft4_fftw_hints "${_ft4_fftw_prefix_from_lib}")
endif()
if(APPLE)
  list(APPEND _ft4_fftw_hints /opt/homebrew/opt/fftw /usr/local/opt/fftw)
endif()

find_path(FFTW3_INCLUDE_DIR NAMES fftw3.h
  HINTS ${_ft4_fftw_hints}
  PATH_SUFFIXES include
)
if(NOT FFTW3_INCLUDE_DIR)
  message(FATAL_ERROR "fftw3.h not found; install FFTW dev package or set FFTW3_PREFIX")
endif()
target_include_directories(ft4_coder PRIVATE ${FFTW3_INCLUDE_DIR})

set(_ft4_fftw_lib_hints)
foreach(_hint IN LISTS _ft4_fftw_hints)
  list(APPEND _ft4_fftw_lib_hints "${_hint}/lib")
endforeach()

if(FFTW3F_LIB)
  if(NOT EXISTS "${FFTW3F_LIB}")
    message(FATAL_ERROR "FFTW3F_LIB does not exist: ${FFTW3F_LIB}")
  endif()
else()
  find_library(FFTW3F_LIB NAMES fftw3f libfftw3f HINTS ${_ft4_fftw_lib_hints} REQUIRED)
endif()

target_link_libraries(ft4_coder PUBLIC ${FFTW3F_LIB} Threads::Threads)

if(FFTW3F_THREADS_LIB)
  if(NOT EXISTS "${FFTW3F_THREADS_LIB}")
    message(FATAL_ERROR "FFTW3F_THREADS_LIB does not exist: ${FFTW3F_THREADS_LIB}")
  endif()
else()
  find_library(FFTW3F_THREADS_LIB NAMES fftw3f_threads libfftw3f_threads HINTS ${_ft4_fftw_lib_hints})
endif()
if(FFTW3F_THREADS_LIB)
  target_link_libraries(ft4_coder PUBLIC ${FFTW3F_THREADS_LIB})
endif()

if(CMAKE_CXX_IMPLICIT_LINK_LIBRARIES)
  target_link_libraries(ft4_coder PUBLIC ${CMAKE_CXX_IMPLICIT_LINK_LIBRARIES})
endif()

if(WIN32)
  target_link_libraries(ft4_coder PUBLIC quadmath)
endif()

if(CMAKE_Fortran_IMPLICIT_LINK_LIBRARIES)
  target_link_libraries(ft4_coder PUBLIC ${CMAKE_Fortran_IMPLICIT_LINK_LIBRARIES})
endif()

add_executable(ft4_smoke smoke/ft4_smoke.c)
target_include_directories(ft4_smoke PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(ft4_smoke PRIVATE ft4_coder)

enable_testing()
add_test(NAME encode_smoke COMMAND ft4_smoke)
