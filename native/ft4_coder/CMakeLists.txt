cmake_minimum_required(VERSION 3.16)
project(ft4_coder C Fortran)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

include(FetchContent)
set(WSJTX_LIB_GIT_TAG "5872315ecfa5a8d0f9bb43e1439d36788213d4a3" CACHE STRING "wsjtx_lib commit")
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

file(GLOB_RECURSE WSJTX_FORTRAN
  CONFIGURE_DEPENDS
  "${WSJTX_LIB_DIR}/lib/*.f90"
  "${WSJTX_LIB_DIR}/lib/*.f"
)
file(GLOB_RECURSE WSJTX_C
  CONFIGURE_DEPENDS
  "${WSJTX_LIB_DIR}/lib/*.c"
)
file(GLOB WSJTX_CPP
  "${WSJTX_LIB_DIR}/lib/crc10.cpp"
  "${WSJTX_LIB_DIR}/lib/crc13.cpp"
  "${WSJTX_LIB_DIR}/lib/crc14.cpp"
)

add_library(ft4_coder SHARED
  src/ft4_coder_shim.c
  ${WSJTX_FORTRAN}
  ${WSJTX_C}
  ${WSJTX_CPP}
)

target_compile_definitions(ft4_coder PRIVATE FT4_CODER_BUILD)
if(NOT WIN32)
  set_target_properties(ft4_coder PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
  )
endif()

target_include_directories(ft4_coder
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${WSJTX_LIB_DIR}
    ${WSJTX_LIB_DIR}/lib
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
find_library(FFTW3F_LIB NAMES fftw3f libfftw3f REQUIRED)

target_link_libraries(ft4_coder PRIVATE ${FFTW3F_LIB} Threads::Threads)

if(NOT WIN32)
  find_library(FFTW3F_THREADS_LIB NAMES fftw3f_threads libfftw3f_threads)
  if(FFTW3F_THREADS_LIB)
    target_link_libraries(ft4_coder PRIVATE ${FFTW3F_THREADS_LIB})
  endif()
endif()

if(WIN32)
  target_link_libraries(ft4_coder PRIVATE quadmath)
endif()

if(CMAKE_Fortran_IMPLICIT_LINK_LIBRARIES)
  target_link_libraries(ft4_coder PRIVATE ${CMAKE_Fortran_IMPLICIT_LINK_LIBRARIES})
endif()

add_executable(ft4_smoke smoke/ft4_smoke.c)
target_include_directories(ft4_smoke PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(ft4_smoke PRIVATE ft4_coder)

enable_testing()
add_test(NAME encode_smoke COMMAND ft4_smoke)
