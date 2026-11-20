cmake_minimum_required(VERSION 3.24)
project(WinWindowNoFocusLoss LANGUAGES C CXX RC)

# Match MinHook's original selection of the instruction decoder per target.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(decoder minhook/src/hde/hde64.c)
else()
    set(decoder minhook/src/hde/hde32.c)
endif()
add_library(minhook STATIC
    minhook/src/buffer.c
    minhook/src/hook.c
    minhook/src/trampoline.c
    ${decoder}
)
target_include_directories(minhook PUBLIC minhook/include)

add_library(NoFocusLoss SHARED NoFocusLoss.cpp NoFocusLoss.rc)
target_compile_definitions(NoFocusLoss PRIVATE UNICODE _UNICODE)
target_link_libraries(NoFocusLoss PRIVATE minhook comctl32 user32)
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set_target_properties(NoFocusLoss PROPERTIES OUTPUT_NAME NoFocusLoss64)
endif()

include(CTest)
if(BUILD_TESTING)
    if(CMAKE_SIZEOF_VOID_P EQUAL 8)
        add_executable(AudioFixture WIN32 ../../tests/WindowRecovery/AudioFixture.cpp)
        target_compile_features(AudioFixture PRIVATE cxx_std_17)
        target_link_libraries(AudioFixture PRIVATE ole32 user32)
    endif()
    add_executable(FocusFixture ../../tests/NoFocusLoss/FocusFixture.cpp)
    target_compile_features(FocusFixture PRIVATE cxx_std_17)
    target_link_libraries(FocusFixture PRIVATE user32)
    add_test(NAME OriginalNoFocusLoss COMMAND FocusFixture "$<TARGET_FILE:NoFocusLoss>")
    set_tests_properties(OriginalNoFocusLoss PROPERTIES TIMEOUT 20)
endif()
