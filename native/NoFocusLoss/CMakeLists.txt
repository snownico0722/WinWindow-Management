cmake_minimum_required(VERSION 3.24)
project(WinWindowNoFocusLoss LANGUAGES C CXX RC)

# The MinHook revision recorded in snownico0722/NoFocusLoss at a88dd66.
include(FetchContent)
FetchContent_Declare(minhook
    URL https://codeload.github.com/TsudaKageyu/minhook/tar.gz/f5485b8454544c2f034c78f8f127c1d03dea3636
    URL_HASH SHA256=9a60e6615ddfe4ba0ef53f8589aa51acb539d617ce106ef72bfeed5a125f758b
)
# The pinned dependency declares CMake 3.0 in its project.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
FetchContent_MakeAvailable(minhook)

add_library(NoFocusLoss SHARED NoFocusLoss.cpp NoFocusLoss.rc)
target_compile_definitions(NoFocusLoss PRIVATE UNICODE _UNICODE)
target_link_libraries(NoFocusLoss PRIVATE minhook comctl32 user32)
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set_target_properties(NoFocusLoss PROPERTIES OUTPUT_NAME NoFocusLoss64)
endif()

include(CTest)
if(BUILD_TESTING)
    add_executable(FocusFixture ../../tests/NoFocusLoss/FocusFixture.cpp)
    target_compile_features(FocusFixture PRIVATE cxx_std_17)
    target_link_libraries(FocusFixture PRIVATE user32)
    add_test(NAME OriginalNoFocusLoss COMMAND FocusFixture "$<TARGET_FILE:NoFocusLoss>")
    set_tests_properties(OriginalNoFocusLoss PROPERTIES TIMEOUT 20)
endif()
