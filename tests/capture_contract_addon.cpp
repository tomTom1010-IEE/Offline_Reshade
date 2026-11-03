#include <reshade.hpp>

static wchar_t event_path[32768] = {};
static void on_screenshot(reshade::api::effect_runtime *, const char *)
{
    const HANDLE file = CreateFileW(event_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    constexpr char marker[] = "screenshot\n";
    WriteFile(file, marker, sizeof(marker) - 1, &written, nullptr);
    CloseHandle(file);
}
extern "C" __declspec(dllexport) const char *NAME = "Offline capture contract test";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Test fixture for screenshot event delivery; never included in release packages.";
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        GetModuleFileNameW(module, event_path, 32750);
        wcscat_s(event_path, L".events");
        if (!reshade::register_addon(module)) return FALSE;
        reshade::register_event<reshade::addon_event::reshade_screenshot>(on_screenshot);
    }
    else if (reason == DLL_PROCESS_DETACH) reshade::unregister_addon(module);
    return TRUE;
}
