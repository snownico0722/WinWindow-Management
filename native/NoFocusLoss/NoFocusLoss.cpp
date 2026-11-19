#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <CommCtrl.h>
#include <TlHelp32.h>
#include "MinHook.h"

#pragma comment(lib, "Comctl32.lib")

namespace
{
    constexpr DWORD kInitializeFailed = 0;
    constexpr DWORD kInitializeSucceeded = 1;
    constexpr DWORD kInitializeUnsafeToUnload = 2;
    constexpr UINT kSubclassTimeoutMs = 2000;
    constexpr wchar_t kInjectedWindowProperty[] = L"NoFocusLoss_Injected_8A11C8C7";

    enum class SubclassChangeResult
    {
        Succeeded,
        Failed,
        TimedOut
    };

    struct EnumWindowsCallbackArgs
    {
        HWND hwnd = nullptr;
        long long area = -1;
    };

    static BOOL CALLBACK EnumWindowsCallback(HWND hnd, LPARAM lParam)
    {
        auto* args = reinterpret_cast<EnumWindowsCallbackArgs*>(lParam);

        RECT rect{};
        if (!GetWindowRect(hnd, &rect))
            return TRUE;

        const long long width = static_cast<long long>(rect.right) - rect.left;
        const long long height = static_cast<long long>(rect.bottom) - rect.top;
        const long long area = width * height;

        if (area > args->area)
        {
            args->area = area;
            args->hwnd = hnd;
        }

        return TRUE;
    }

    template<class TCallback>
    bool VisitProcessThreads(TCallback visitor)
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return false;

        THREADENTRY32 entry{};
        entry.dwSize = sizeof(entry);

        if (!Thread32First(snapshot, &entry))
        {
            CloseHandle(snapshot);
            return false;
        }

        do
        {
            visitor(entry);
        } while (Thread32Next(snapshot, &entry));

        CloseHandle(snapshot);
        return true;
    }

    HWND GetMainWindow()
    {
        const DWORD currentProcessId = GetCurrentProcessId();
        EnumWindowsCallbackArgs args{};

        VisitProcessThreads([&](const THREADENTRY32& threadEntry)
        {
            if (threadEntry.th32OwnerProcessID != currentProcessId)
                return;

            EnumThreadWindows(threadEntry.th32ThreadID, EnumWindowsCallback,
                              reinterpret_cast<LPARAM>(&args));
        });

        return args.hwnd;
    }

    template <typename T>
    MH_STATUS MH_CreateHookEx(LPVOID target, LPVOID detour, T** original)
    {
        return MH_CreateHook(target, detour, reinterpret_cast<LPVOID*>(original));
    }

    HMODULE g_module = nullptr;
    HWND g_mainWindow = nullptr;
    DWORD g_uiThreadId = 0;
    UINT g_subclassMessage = 0;

    volatile LONG g_initialized = FALSE;
    volatile LONG g_enabled = FALSE;
    volatile LONG g_unfocused = FALSE;
    volatile LONG g_subclassInstalled = FALSE;
    volatile LONG g_blockCursorWhenUnfocused = TRUE;
    volatile LONG g_operationInProgress = FALSE;
    volatile LONG g_unsafeToUnload = FALSE;
    volatile LONG g_subclassOperation = 0;
    volatile LONG g_subclassResult = FALSE;

    static decltype(GetForegroundWindow)* real_GetForegroundWindow = GetForegroundWindow;
    static decltype(GetActiveWindow)* real_GetActiveWindow = GetActiveWindow;
    static decltype(GetFocus)* real_GetFocus = GetFocus;
    static decltype(SetCursorPos)* real_SetCursorPos = SetCursorPos;

    bool ReadFlag(volatile LONG* flag)
    {
        return InterlockedCompareExchange(flag, FALSE, FALSE) != FALSE;
    }

    bool IsEnabled()
    {
        return ReadFlag(&g_enabled);
    }

    void SetUnfocused(bool value)
    {
        InterlockedExchange(&g_unfocused, value ? TRUE : FALSE);
    }

    void SetInjectedMarker()
    {
        if (g_mainWindow)
            SetPropW(g_mainWindow, kInjectedWindowProperty, reinterpret_cast<HANDLE>(1));
    }

    void ClearInjectedMarker()
    {
        if (g_mainWindow)
            RemovePropW(g_mainWindow, kInjectedWindowProperty);
    }

    HWND WINAPI DetourGetForegroundWindow()
    {
        if (IsEnabled() && g_mainWindow)
            return g_mainWindow;

        return real_GetForegroundWindow();
    }

    HWND WINAPI DetourGetActiveWindow()
    {
        if (!IsEnabled() || !g_mainWindow)
            return real_GetActiveWindow();

        if (GetCurrentThreadId() == g_uiThreadId)
            return g_mainWindow;

        return nullptr;
    }

    HWND WINAPI DetourGetFocus()
    {
        if (!IsEnabled() || !g_mainWindow)
            return real_GetFocus();

        if (GetCurrentThreadId() == g_uiThreadId)
            return g_mainWindow;

        return nullptr;
    }

    BOOL WINAPI DetourSetCursorPos(int x, int y)
    {
        const bool blockCursor = ReadFlag(&g_blockCursorWhenUnfocused);
        const bool unfocused = ReadFlag(&g_unfocused);

        if (IsEnabled() && blockCursor && unfocused)
            return TRUE;

        return real_SetCursorPos(x, y);
    }

    LRESULT CALLBACK GameWindowSubclassProc(HWND hWnd, UINT message, WPARAM wParam,
                                            LPARAM lParam, UINT_PTR, DWORD_PTR)
    {
        if (!IsEnabled() || hWnd != g_mainWindow)
            return DefSubclassProc(hWnd, message, wParam, lParam);

        switch (message)
        {
        case WM_SETFOCUS:
            SetUnfocused(false);
            break;

        case WM_KILLFOCUS:
            SetUnfocused(true);
            return 0;

        case WM_ACTIVATEAPP:
            if (wParam == FALSE)
            {
                SetUnfocused(true);
                return 0;
            }
            SetUnfocused(false);
            break;

        case WM_ACTIVATE:
            if (LOWORD(wParam) == WA_INACTIVE)
            {
                SetUnfocused(true);
                return 0;
            }
            SetUnfocused(false);
            break;

        case WM_NCACTIVATE:
            // Do not swallow this. Blocking it can break normal window behavior,
            // including minimize/restore handling in some programs.
            break;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    LRESULT CALLBACK CallWndProcForSubclass(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION)
        {
            const auto* message = reinterpret_cast<const CWPSTRUCT*>(lParam);
            if (message->message == g_subclassMessage)
            {
                const LONG operation = InterlockedCompareExchange(&g_subclassOperation, 0, 0);
                if (operation == 1)
                {
                    InterlockedExchange(&g_subclassResult,
                        SetWindowSubclass(message->hwnd, GameWindowSubclassProc, 0, 0));
                }
                else if (operation == 2)
                {
                    InterlockedExchange(&g_subclassResult,
                        RemoveWindowSubclass(message->hwnd, GameWindowSubclassProc, 0));
                }
            }
        }

        return CallNextHookEx(nullptr, code, wParam, lParam);
    }

    SubclassChangeResult SetMainWindowSubclass(bool install)
    {
        if (!g_mainWindow || !g_subclassMessage)
            return SubclassChangeResult::Failed;

        const DWORD threadId = GetWindowThreadProcessId(g_mainWindow, nullptr);
        if (!threadId)
            return SubclassChangeResult::Failed;

        if (threadId == GetCurrentThreadId())
        {
            const BOOL result = install
                ? SetWindowSubclass(g_mainWindow, GameWindowSubclassProc, 0, 0)
                : RemoveWindowSubclass(g_mainWindow, GameWindowSubclassProc, 0);
            return result ? SubclassChangeResult::Succeeded : SubclassChangeResult::Failed;
        }

        HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, CallWndProcForSubclass, nullptr, threadId);
        if (!hook)
            return SubclassChangeResult::Failed;

        InterlockedExchange(&g_subclassResult, FALSE);
        InterlockedExchange(&g_subclassOperation, install ? 1 : 2);

        DWORD_PTR messageResult = 0;
        const LRESULT sendResult = SendMessageTimeoutW(
            g_mainWindow,
            g_subclassMessage,
            0,
            0,
            SMTO_ABORTIFHUNG | SMTO_BLOCK,
            kSubclassTimeoutMs,
            &messageResult);

        InterlockedExchange(&g_subclassOperation, 0);
        UnhookWindowsHookEx(hook);

        if (!sendResult)
            return SubclassChangeResult::TimedOut;

        return ReadFlag(&g_subclassResult)
            ? SubclassChangeResult::Succeeded
            : SubclassChangeResult::Failed;
    }

    bool DisableMinHook()
    {
        const MH_STATUS disableStatus = MH_DisableHook(MH_ALL_HOOKS);
        const MH_STATUS uninitStatus = MH_Uninitialize();

        const bool disableOk = disableStatus == MH_OK || disableStatus == MH_ERROR_DISABLED ||
                               disableStatus == MH_ERROR_NOT_CREATED ||
                               disableStatus == MH_ERROR_NOT_INITIALIZED;
        const bool uninitOk = uninitStatus == MH_OK || uninitStatus == MH_ERROR_NOT_INITIALIZED;
        return disableOk && uninitOk;
    }

    DWORD InitializeNoFocusLoss()
    {
        if (ReadFlag(&g_unsafeToUnload))
            return kInitializeUnsafeToUnload;

        if (ReadFlag(&g_initialized))
            return kInitializeSucceeded;

        if (InterlockedCompareExchange(&g_operationInProgress, TRUE, FALSE) != FALSE)
            return kInitializeFailed;

        DWORD result = kInitializeFailed;
        bool minHookInitialized = false;

        do
        {
            g_mainWindow = GetMainWindow();
            if (!g_mainWindow)
                break;

            // The marker is written from inside the target process, so UIPI cannot block it.
            // Keep it set for any state where the DLL remains loaded, including failed init.
            SetInjectedMarker();

            g_uiThreadId = GetWindowThreadProcessId(g_mainWindow, nullptr);
            if (!g_uiThreadId)
                break;

            g_subclassMessage = RegisterWindowMessageW(
                L"NoFocusLoss_SetMainWindowSubclass_8A11C8C7");
            if (!g_subclassMessage)
                break;

            InterlockedExchange(&g_blockCursorWhenUnfocused, TRUE);

            const MH_STATUS initStatus = MH_Initialize();
            if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
                break;
            minHookInitialized = true;

            if (MH_CreateHookEx(GetForegroundWindow, DetourGetForegroundWindow,
                                &real_GetForegroundWindow) != MH_OK ||
                MH_CreateHookEx(GetActiveWindow, DetourGetActiveWindow,
                                &real_GetActiveWindow) != MH_OK ||
                MH_CreateHookEx(GetFocus, DetourGetFocus,
                                &real_GetFocus) != MH_OK ||
                MH_CreateHookEx(SetCursorPos, DetourSetCursorPos,
                                &real_SetCursorPos) != MH_OK)
            {
                break;
            }

            if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
                break;

            const SubclassChangeResult subclassResult = SetMainWindowSubclass(true);
            if (subclassResult == SubclassChangeResult::TimedOut)
            {
                InterlockedExchange(&g_unsafeToUnload, TRUE);
                result = kInitializeUnsafeToUnload;
                break;
            }
            if (subclassResult != SubclassChangeResult::Succeeded)
                break;

            InterlockedExchange(&g_subclassInstalled, TRUE);
            SetUnfocused(false);
            InterlockedExchange(&g_enabled, TRUE);
            InterlockedExchange(&g_initialized, TRUE);
            result = kInitializeSucceeded;
        } while (false);

        if (result != kInitializeSucceeded && minHookInitialized)
        {
            InterlockedExchange(&g_enabled, FALSE);
            if (!DisableMinHook())
            {
                InterlockedExchange(&g_unsafeToUnload, TRUE);
                result = kInitializeUnsafeToUnload;
            }
        }

        InterlockedExchange(&g_operationInProgress, FALSE);
        return result;
    }

    bool ShutdownNoFocusLoss()
    {
        if (ReadFlag(&g_unsafeToUnload))
            return false;

        if (!ReadFlag(&g_initialized))
        {
            ClearInjectedMarker();
            return true;
        }

        if (InterlockedCompareExchange(&g_operationInProgress, TRUE, FALSE) != FALSE)
            return false;

        // From this point on, even a failed shutdown leaves the feature inert.
        InterlockedExchange(&g_enabled, FALSE);

        if (ReadFlag(&g_subclassInstalled))
        {
            const SubclassChangeResult subclassResult = SetMainWindowSubclass(false);
            if (subclassResult == SubclassChangeResult::TimedOut)
            {
                InterlockedExchange(&g_unsafeToUnload, TRUE);
                InterlockedExchange(&g_operationInProgress, FALSE);
                return false;
            }
            if (subclassResult != SubclassChangeResult::Succeeded)
            {
                InterlockedExchange(&g_operationInProgress, FALSE);
                return false;
            }

            InterlockedExchange(&g_subclassInstalled, FALSE);
        }

        if (!DisableMinHook())
        {
            InterlockedExchange(&g_unsafeToUnload, TRUE);
            InterlockedExchange(&g_operationInProgress, FALSE);
            return false;
        }

        ClearInjectedMarker();
        g_mainWindow = nullptr;
        g_uiThreadId = 0;
        SetUnfocused(false);
        InterlockedExchange(&g_initialized, FALSE);
        InterlockedExchange(&g_operationInProgress, FALSE);
        return true;
    }
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_Initialize(LPVOID)
{
    return InitializeNoFocusLoss();
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_DisableCursorBlocking(LPVOID)
{
    InterlockedExchange(&g_blockCursorWhenUnfocused, FALSE);
    return TRUE;
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_ShutdownAndUnload(LPVOID)
{
    if (!ShutdownNoFocusLoss() || !g_module)
        return FALSE;

    FreeLibraryAndExitThread(g_module, TRUE);
    return FALSE;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = hModule;
        DisableThreadLibraryCalls(hModule);
    }

    return TRUE;
}
