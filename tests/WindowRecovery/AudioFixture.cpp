#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <wrl/client.h>
#include <array>

using Microsoft::WRL::ComPtr;

struct Session
{
    ComPtr<IAudioClient> client;
    ComPtr<ISimpleAudioVolume> volume;

    HRESULT Open(IMMDevice* device, BOOL muted)
    {
        HRESULT result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf()));
        if (FAILED(result)) return result;
        WAVEFORMATEX* format = nullptr;
        result = client->GetMixFormat(&format);
        if (FAILED(result)) return result;
        GUID session{};
        result = CoCreateGuid(&session);
        if (SUCCEEDED(result))
            result = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 1000000, 0, format, &session);
        CoTaskMemFree(format);
        if (FAILED(result)) return result;
        ComPtr<IAudioRenderClient> render;
        result = client->GetService(IID_PPV_ARGS(&render));
        if (FAILED(result)) return result;
        result = client->GetService(IID_PPV_ARGS(&volume));
        if (FAILED(result)) return result;
        result = volume->SetMute(muted, nullptr);
        if (FAILED(result)) return result;
        return client->Start();
    }
};

static std::array<Session, 2> sessions;
static bool hasAudio;

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp)
{
    if (message == WM_TIMER && hasAudio)
    {
        BOOL music = FALSE, voice = FALSE;
        if (SUCCEEDED(sessions[0].volume->GetMute(&music)) && SUCCEEDED(sessions[1].volume->GetMute(&voice)))
            SetPropW(window, L"WinWindow.Test.Mix", reinterpret_cast<HANDLE>(1 + (music ? 1 : 0) + (voice ? 2 : 0)));
        return 0;
    }
    if (message == WM_APP + 91)
    {
        SetPropW(window, L"WinWindow.Test.Stalled", reinterpret_cast<HANDLE>(1));
        Sleep(3500);
        RemovePropW(window, L"WinWindow.Test.Stalled");
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wp, lp);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 1;
    ComPtr<IMMDeviceEnumerator> devices;
    ComPtr<IMMDevice> output;
    HRESULT result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&devices));
    if (SUCCEEDED(result)) result = devices->GetDefaultAudioEndpoint(eRender, eMultimedia, &output);
    hasAudio = SUCCEEDED(result);
    if (!hasAudio && result != HRESULT_FROM_WIN32(ERROR_NOT_FOUND) && result != AUDCLNT_E_SERVICE_NOT_RUNNING)
        return 2;
    if (hasAudio && (FAILED(sessions[0].Open(output.Get(), TRUE)) || FAILED(sessions[1].Open(output.Get(), FALSE))))
        return 3;

    WNDCLASSW cls{};
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = instance;
    cls.lpszClassName = L"WinWindowRecoveryFixture";
    if (!RegisterClassW(&cls)) return 4;
    HWND window = CreateWindowW(cls.lpszClassName, L"WinWindow session recovery fixture", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 360, 200, nullptr, nullptr, instance, nullptr);
    if (!window) return 5;
    if (!hasAudio) SetPropW(window, L"WinWindow.Test.NoEndpoint", reinterpret_cast<HANDLE>(1));
    SetTimer(window, 1, 30, nullptr);
    ShowWindow(window, SW_SHOWNOACTIVATE);
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    for (auto& session : sessions) { session.volume.Reset(); session.client.Reset(); }
    output.Reset(); devices.Reset();
    CoUninitialize();
    return 0;
}
