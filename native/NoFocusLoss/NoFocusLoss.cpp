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

    HWND ResolveTargetWindow(LPVOID parameter)
    {
        if (parameter)
        {
            HWND candidate = static_cast<HWND>(parameter);
            DWORD processId = 0;
            if (IsWindow(candidate) &&
                GetWindowThreadProcessId(candidate, &processId) != 0 &&
                processId == GetCurrentProcessId())
            {
                return candidate;
            }
            return nullptr;
        }

        return GetMainWindow();
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
    volatile LONG g_lockCursorPosWhenUnfocused = FALSE;
    volatile LONG g_hasLockedCursorPos = FALSE;
    volatile LONG g_lockedCursorX = 0;
    volatile LONG g_lockedCursorY = 0;
    SRWLOCK g_cursorClipLock = SRWLOCK_INIT;
    RECT g_ownedCursorClip{};
    bool g_hasOwnedCursorClip = false;
    volatile LONG g_operationInProgress = FALSE;
    volatile LONG g_unsafeToUnload = FALSE;
    volatile LONG g_subclassOperation = 0;
    volatile LONG g_subclassResult = FALSE;

    static decltype(GetForegroundWindow)* real_GetForegroundWindow = GetForegroundWindow;
    static decltype(GetActiveWindow)* real_GetActiveWindow = GetActiveWindow;
    static decltype(GetFocus)* real_GetFocus = GetFocus;
    static decltype(GetGUIThreadInfo)* real_GetGUIThreadInfo = GetGUIThreadInfo;
    static decltype(SetCursorPos)* real_SetCursorPos = SetCursorPos;
    static decltype(ClipCursor)* real_ClipCursor = ClipCursor;
    static decltype(GetCursorPos)* real_GetCursorPos = GetCursorPos;
    static decltype(GetAsyncKeyState)* real_GetAsyncKeyState = GetAsyncKeyState;
    static decltype(GetKeyState)* real_GetKeyState = GetKeyState;

    void ResetRealPointers()
    {
        real_GetForegroundWindow = GetForegroundWindow;
        real_GetActiveWindow = GetActiveWindow;
        real_GetFocus = GetFocus;
        real_GetGUIThreadInfo = GetGUIThreadInfo;
        real_SetCursorPos = SetCursorPos;
        real_ClipCursor = ClipCursor;
        real_GetCursorPos = GetCursorPos;
        real_GetAsyncKeyState = GetAsyncKeyState;
        real_GetKeyState = GetKeyState;
    }

    bool ReadFlag(volatile LONG* flag)
    {
        return InterlockedCompareExchange(flag, FALSE, FALSE) != FALSE;
    }

    bool IsEnabled()
    {
        return ReadFlag(&g_enabled);
    }

    bool IsWindowActuallyForeground()
    {
        if (!g_mainWindow)
            return false;

        HWND foreground = real_GetForegroundWindow();
        if (!foreground)
            return false;
        if (foreground == g_mainWindow || IsChild(g_mainWindow, foreground))
            return true;

        DWORD processId = 0;
        return GetWindowThreadProcessId(foreground, &processId) != 0 &&
               processId == GetCurrentProcessId();
    }

    bool IsCurrentlyUnfocused()
    {
        if (IsWindowActuallyForeground())
        {
            if (ReadFlag(&g_unfocused))
                InterlockedExchange(&g_unfocused, FALSE);
            return false;
        }
        return true;
    }

    bool TryGetSafeInteriorBox(RECT* safeBox, POINT* center)
    {
        if (!g_mainWindow || !IsWindow(g_mainWindow))
            return false;

        RECT bounds{};
        RECT client{};
        POINT topLeft{}, bottomRight{};
        if (GetClientRect(g_mainWindow, &client) &&
            client.right > client.left && client.bottom > client.top &&
            ClientToScreen(g_mainWindow, &topLeft))
        {
            bottomRight.x = client.right;
            bottomRight.y = client.bottom;
            if (ClientToScreen(g_mainWindow, &bottomRight) &&
                bottomRight.x > topLeft.x && bottomRight.y > topLeft.y)
            {
                bounds = { topLeft.x, topLeft.y, bottomRight.x, bottomRight.y };
            }
        }

        if (bounds.right <= bounds.left || bounds.bottom <= bounds.top)
        {
            if (!GetWindowRect(g_mainWindow, &bounds) ||
                bounds.right <= bounds.left || bounds.bottom <= bounds.top)
            {
                return false;
            }
        }

        const LONG width = bounds.right - bounds.left;
        const LONG height = bounds.bottom - bounds.top;
        const LONG marginX = (width * 15) / 100;
        const LONG marginY = (height * 15) / 100;
        if (safeBox)
        {
            safeBox->left = bounds.left + marginX;
            safeBox->top = bounds.top + marginY;
            safeBox->right = bounds.right - marginX;
            safeBox->bottom = bounds.bottom - marginY;
            if (safeBox->right <= safeBox->left)
            {
                safeBox->left = bounds.left;
                safeBox->right = bounds.right;
            }
            if (safeBox->bottom <= safeBox->top)
            {
                safeBox->top = bounds.top;
                safeBox->bottom = bounds.bottom;
            }
        }
        if (center)
        {
            center->x = bounds.left + width / 2;
            center->y = bounds.top + height / 2;
        }
        return true;
    }

    void RememberSafeCursorPoint(const POINT& pt)
    {
        RECT safe{};
        if (!TryGetSafeInteriorBox(&safe, nullptr))
            return;

        if (pt.x >= safe.left && pt.x < safe.right && pt.y >= safe.top && pt.y < safe.bottom)
        {
            InterlockedExchange(&g_lockedCursorX, pt.x);
            InterlockedExchange(&g_lockedCursorY, pt.y);
            InterlockedExchange(&g_hasLockedCursorPos, TRUE);
        }
    }

    void RefreshLockedCursorPosition()
    {
        POINT current{};
        if (IsWindowActuallyForeground() && real_GetCursorPos(&current))
        {
            RememberSafeCursorPoint(current);
            if (ReadFlag(&g_hasLockedCursorPos))
                return;
        }

        POINT center{};
        if (TryGetSafeInteriorBox(nullptr, &center))
        {
            InterlockedExchange(&g_lockedCursorX, center.x);
            InterlockedExchange(&g_lockedCursorY, center.y);
            InterlockedExchange(&g_hasLockedCursorPos, TRUE);
        }
    }

    void ReleaseWindowCursorClip()
    {
        AcquireSRWLockExclusive(&g_cursorClipLock);
        RECT current{};
        if (g_hasOwnedCursorClip && GetClipCursor(&current) && EqualRect(&current, &g_ownedCursorClip))
            real_ClipCursor(nullptr);
        g_hasOwnedCursorClip = false;
        ReleaseSRWLockExclusive(&g_cursorClipLock);
    }

    void SetUnfocused(bool value)
    {
        const LONG previous = InterlockedExchange(&g_unfocused, value ? TRUE : FALSE);
        if (value && previous == FALSE)
        {
            if (ReadFlag(&g_lockCursorPosWhenUnfocused) && !ReadFlag(&g_hasLockedCursorPos))
                RefreshLockedCursorPosition();
            ReleaseWindowCursorClip();
        }
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

    bool ShouldSpoofFocus()
    {
        return IsEnabled() && g_mainWindow && IsCurrentlyUnfocused();
    }

    HWND WINAPI DetourGetForegroundWindow()
    {
        if (ShouldSpoofFocus())
            return g_mainWindow;

        return real_GetForegroundWindow();
    }

    HWND WINAPI DetourGetActiveWindow()
    {
        if (!ShouldSpoofFocus())
            return real_GetActiveWindow();

        if (GetCurrentThreadId() == g_uiThreadId)
            return g_mainWindow;

        return nullptr;
    }

    HWND WINAPI DetourGetFocus()
    {
        if (!ShouldSpoofFocus())
            return real_GetFocus();

        if (GetCurrentThreadId() == g_uiThreadId)
            return g_mainWindow;

        return nullptr;
    }

    BOOL WINAPI DetourGetGUIThreadInfo(DWORD idThread, PGUITHREADINFO pgui)
    {
        const BOOL ok = real_GetGUIThreadInfo(idThread, pgui);
        if (!ok || !pgui || !ShouldSpoofFocus())
            return ok;

        const bool isTargetUiThread = idThread == 0
            ? GetCurrentThreadId() == g_uiThreadId
            : idThread == g_uiThreadId;
        if (isTargetUiThread)
        {
            pgui->hwndActive = g_mainWindow;
            pgui->hwndFocus = g_mainWindow;
        }
        return TRUE;
    }

    BOOL WINAPI DetourSetCursorPos(int x, int y)
    {
        if (IsEnabled() && ReadFlag(&g_blockCursorWhenUnfocused) && IsCurrentlyUnfocused())
            return TRUE;

        return real_SetCursorPos(x, y);
    }

    BOOL WINAPI DetourClipCursor(const RECT* rect)
    {
        if (!IsEnabled() || !ReadFlag(&g_blockCursorWhenUnfocused))
            return real_ClipCursor(rect);

        AcquireSRWLockExclusive(&g_cursorClipLock);
        if (IsCurrentlyUnfocused())
        {
            ReleaseSRWLockExclusive(&g_cursorClipLock);
            if (rect == nullptr)
                ReleaseWindowCursorClip();
            return TRUE;
        }

        const BOOL ok = real_ClipCursor(rect);
        if (ok)
            g_hasOwnedCursorClip = rect != nullptr && GetClipCursor(&g_ownedCursorClip);
        ReleaseSRWLockExclusive(&g_cursorClipLock);
        return ok;
    }

    BOOL WINAPI DetourGetCursorPos(LPPOINT point)
    {
        if (!IsEnabled() || !point || !ReadFlag(&g_lockCursorPosWhenUnfocused))
            return real_GetCursorPos(point);

        if (!IsCurrentlyUnfocused())
        {
            const BOOL ok = real_GetCursorPos(point);
            if (ok)
                RememberSafeCursorPoint(*point);
            return ok;
        }

        RECT safe{};
        POINT center{};
        if (!TryGetSafeInteriorBox(&safe, &center))
            return real_GetCursorPos(point);

        if (ReadFlag(&g_hasLockedCursorPos))
        {
            const LONG x = InterlockedCompareExchange(&g_lockedCursorX, 0, 0);
            const LONG y = InterlockedCompareExchange(&g_lockedCursorY, 0, 0);
            if (x >= safe.left && x < safe.right && y >= safe.top && y < safe.bottom)
            {
                point->x = x;
                point->y = y;
                return TRUE;
            }
        }

        point->x = center.x;
        point->y = center.y;
        return TRUE;
    }

    SHORT WINAPI DetourGetAsyncKeyState(int vKey)
    {
        if (IsEnabled() && IsCurrentlyUnfocused())
            return 0;

        return real_GetAsyncKeyState(vKey);
    }

    SHORT WINAPI DetourGetKeyState(int nVirtKey)
    {
        const SHORT state = real_GetKeyState(nVirtKey);
        if (IsEnabled() && IsCurrentlyUnfocused())
            return static_cast<SHORT>(state & 0x0001);

        return state;
    }

    LRESULT CALLBACK GameWindowSubclassProc(HWND hWnd, UINT message, WPARAM wParam,
                                            LPARAM lParam, UINT_PTR, DWORD_PTR)
    {
        if (hWnd == g_mainWindow && message == WM_NCDESTROY)
        {
            InterlockedExchange(&g_enabled, FALSE);
            ReleaseWindowCursorClip();
            RemoveWindowSubclass(hWnd, GameWindowSubclassProc, 0);
            InterlockedExchange(&g_subclassInstalled, FALSE);
        }

        if (!IsEnabled() || hWnd != g_mainWindow)
            return DefSubclassProc(hWnd, message, wParam, lParam);

        switch (message)
        {
        case WM_SETFOCUS:
            SetUnfocused(!IsWindowActuallyForeground());
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
            SetUnfocused(!IsWindowActuallyForeground());
            break;

        case WM_ACTIVATE:
            if (LOWORD(wParam) == WA_INACTIVE)
            {
                SetUnfocused(true);
                return 0;
            }
            SetUnfocused(!IsWindowActuallyForeground());
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
        if (disableOk && uninitOk)
            ResetRealPointers();
        return disableOk && uninitOk;
    }

    DWORD InitializeNoFocusLoss(LPVOID parameter = nullptr)
    {
        if (ReadFlag(&g_unsafeToUnload))
            return kInitializeUnsafeToUnload;

        if (ReadFlag(&g_initialized))
        {
            return IsEnabled() && (!parameter || parameter == g_mainWindow)
                ? kInitializeSucceeded : kInitializeUnsafeToUnload;
        }

        if (InterlockedCompareExchange(&g_operationInProgress, TRUE, FALSE) != FALSE)
            return kInitializeFailed;

        DWORD result = kInitializeFailed;
        bool minHookInitialized = false;

        do
        {
            g_mainWindow = ResolveTargetWindow(parameter);
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
            InterlockedExchange(&g_lockCursorPosWhenUnfocused, FALSE);
            InterlockedExchange(&g_hasLockedCursorPos, FALSE);

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
                MH_CreateHookEx(GetGUIThreadInfo, DetourGetGUIThreadInfo,
                                &real_GetGUIThreadInfo) != MH_OK ||
                MH_CreateHookEx(SetCursorPos, DetourSetCursorPos,
                                &real_SetCursorPos) != MH_OK ||
                MH_CreateHookEx(ClipCursor, DetourClipCursor,
                                &real_ClipCursor) != MH_OK ||
                MH_CreateHookEx(GetCursorPos, DetourGetCursorPos,
                                &real_GetCursorPos) != MH_OK ||
                MH_CreateHookEx(GetAsyncKeyState, DetourGetAsyncKeyState,
                                &real_GetAsyncKeyState) != MH_OK ||
                MH_CreateHookEx(GetKeyState, DetourGetKeyState,
                                &real_GetKeyState) != MH_OK)
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
            InterlockedExchange(&g_unfocused, FALSE);
            SetUnfocused(!IsWindowActuallyForeground());
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
        ReleaseWindowCursorClip();

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
        InterlockedExchange(&g_unfocused, FALSE);
        InterlockedExchange(&g_lockCursorPosWhenUnfocused, FALSE);
        InterlockedExchange(&g_hasLockedCursorPos, FALSE);
        InterlockedExchange(&g_initialized, FALSE);
        InterlockedExchange(&g_operationInProgress, FALSE);
        return true;
    }
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_Initialize(LPVOID parameter)
{
    return InitializeNoFocusLoss(parameter);
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_DisableCursorBlocking(LPVOID)
{
    InterlockedExchange(&g_blockCursorWhenUnfocused, FALSE);
    return TRUE;
}

extern "C" __declspec(dllexport) DWORD WINAPI NoFocusLoss_SetCursorPositionLock(LPVOID enabled)
{
    if (enabled != nullptr)
        RefreshLockedCursorPosition();
    InterlockedExchange(&g_lockCursorPosWhenUnfocused, enabled != nullptr ? TRUE : FALSE);
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
