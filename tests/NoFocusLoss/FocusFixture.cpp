// A hidden window loads the original library in its own process. A foreign
// foreground window remains available for comparison throughout the test.
#include <windows.h>
#include <cstdio>
#include <stdexcept>

void Check(bool condition, const char* description)
{
    if (!condition) throw std::runtime_error(description);
    std::printf("PASS %s\n", description);
}

unsigned received = 0;

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wp, LPARAM lp)
{
    if (message == WM_KILLFOCUS) { received |= 1; return 0; }
    if (message == WM_ACTIVATEAPP && !wp) { received |= 2; return 0; }
    if (message == WM_ACTIVATE && LOWORD(wp) == WA_INACTIVE) { received |= 4; return 0; }
    return DefWindowProcW(window, message, wp, lp);
}

unsigned SendDeactivation(HWND window)
{
    received = 0;
    SendMessageW(window, WM_KILLFOCUS, 0, 0);
    SendMessageW(window, WM_ACTIVATEAPP, FALSE, 0);
    SendMessageW(window, WM_ACTIVATE, WA_INACTIVE, 0);
    return received;
}

LPTHREAD_START_ROUTINE FindEntry(HMODULE library, const char* name, const char* x86Name)
{
    auto entry = GetProcAddress(library, name);
    if (!entry) entry = GetProcAddress(library, x86Name);
    if (!entry) throw std::runtime_error(name);
    return reinterpret_cast<LPTHREAD_START_ROUTINE>(entry);
}

void CheckCursor(bool blocked)
{
    POINT before{}, after{};
    RECT clip{};
    Check(GetCursorPos(&before) && GetClipCursor(&clip) && clip.right - clip.left > 1,
          "cursor is available on the test desktop");
    const auto x = before.x + (before.x + 1 < clip.right ? 1 : -1);
    bool moved = SetCursorPos(x, before.y) && GetCursorPos(&after);
    if (moved && after.x != before.x) SetCursorPos(before.x, before.y);
    Check(moved && after.x == (blocked ? before.x : x) && after.y == before.y,
          blocked ? "background cursor move is suppressed" : "normal cursor movement works");
}

void CheckUnload(LPTHREAD_START_ROUTINE shutdown)
{
    // The upstream shutdown export exits its calling thread. Keep dispatching
    // the window's messages on the UI thread until the local worker finishes.
    HANDLE worker = CreateThread(nullptr, 0, shutdown, nullptr, 0, nullptr);
    Check(worker != nullptr, "local shutdown worker started");
    DWORD wait;
    while ((wait = MsgWaitForMultipleObjects(1, &worker, FALSE, 3500, QS_ALLINPUT)) == WAIT_OBJECT_0 + 1)
    {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    DWORD result = 0;
    bool unloaded = wait == WAIT_OBJECT_0 && GetExitCodeThread(worker, &result) && result == TRUE;
    CloseHandle(worker);
    Check(unloaded, "original shutdown completed");
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc != 2) throw std::runtime_error("Expected the fixture library path");
        WNDCLASSW cls{};
        cls.lpfnWndProc = WindowProcedure;
        cls.hInstance = GetModuleHandleW(nullptr);
        cls.lpszClassName = L"WinWindowNoFocusLossTest";
        Check(RegisterClassW(&cls) != 0, "window class registered");
        HWND window = CreateWindowW(cls.lpszClassName, L"Focus fixture", WS_OVERLAPPEDWINDOW,
                                    0, 0, 800, 600, nullptr, nullptr, cls.hInstance, nullptr);
        Check(window && GetForegroundWindow() != window && GetFocus() != window, "fixture starts in the background");
        Check(SendDeactivation(window) == 7, "baseline receives all three deactivation messages");
        CheckCursor(false);

        HMODULE library = LoadLibraryW(argv[1]);
        Check(library != nullptr, "fixture loaded its own native library");
        auto initialize = FindEntry(library, "NoFocusLoss_Initialize", "_NoFocusLoss_Initialize@4");
        auto shutdown = FindEntry(library, "NoFocusLoss_ShutdownAndUnload", "_NoFocusLoss_ShutdownAndUnload@4");
        Check(initialize(nullptr) == 1, "original initialization completed");
        Check(GetForegroundWindow() == window && GetActiveWindow() == window && GetFocus() == window,
              "all three UI-thread focus queries return the fixture");
        Check(SendDeactivation(window) == 0, "deactivation messages do not reach the fixture");
        CheckCursor(true);

        CheckUnload(shutdown);
        Check(GetModuleHandleW(argv[1]) == nullptr, "library unloaded from the fixture");
        Check(GetForegroundWindow() != window && GetActiveWindow() != window && GetFocus() != window,
              "foreground, active and focus queries restored");
        Check(SendDeactivation(window) == 7, "all three deactivation messages restored");
        CheckCursor(false);
        DestroyWindow(window);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "FAIL %s\n", error.what());
        return 1;
    }
}
