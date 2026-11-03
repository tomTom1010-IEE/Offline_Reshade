#include <Windows.h>
#include "reshade.hpp"
#include "offline_addon_service.hpp"
#include <regex>
#include <string>
#include <cmath>

extern "C" __declspec(dllexport) const char *NAME = "Independent Service Fixture";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Test-only business API, with no ImGui UI";

extern "C" __declspec(dllexport) const char *OfflineReShadeAddonServiceV1(reshade::api::effect_runtime *runtime, const char *method, const char *request)
{
    static thread_local std::string result;
    auto variable = runtime->find_uniform_variable("ServiceFixture.fx", "Gain");
    if (!variable.handle) return "{\"error\":\"Fixture FX missing\"}";
    float gain = 1; runtime->get_uniform_value_float(variable, &gain, 1);
    const std::string input(request);
    if (std::string(method) == "set") {
        std::smatch match;
        if (!std::regex_search(input, std::regex("\\\"id\\\"\\s*:\\s*\\\"gain\\\"")) || !std::regex_search(input, match, std::regex("\\\"value\\\"\\s*:\\s*\\[\\s*([0-9.eE+-]+)\\s*\\]"))) return "{\"error\":\"Invalid parameter\"}";
        try { gain = std::stof(match[1]); } catch (...) { return "{\"error\":\"Invalid number\"}"; }
        if (!std::isfinite(gain) || gain < 0 || gain > 1) return "{\"error\":\"Out of range\"}";
        runtime->set_uniform_value_float(variable, &gain, 1); runtime->save_current_preset();
    } else if (std::string(method) == "action") {
        if (!std::regex_search(input, std::regex("\\\"id\\\"\\s*:\\s*\\\"reset\\\""))) return "{\"error\":\"Unknown action\"}";
        gain = 1; runtime->set_uniform_value_float(variable, &gain, 1); runtime->save_current_preset();
    } else if (std::string(method) != "get") return "{\"error\":\"Unknown method\"}";
    result = "{\"schemaVersion\":1,\"parameters\":[{\"id\":\"gain\",\"label\":\"Gain\",\"type\":\"float\",\"uiType\":\"slider\",\"min\":0,\"max\":1,\"value\":[" + std::to_string(gain) + "]}],\"actions\":[{\"id\":\"reset\",\"label\":\"Reset gain\"}]}";
    return result.c_str();
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) return reshade::register_addon(module);
    if (reason == DLL_PROCESS_DETACH) reshade::unregister_addon(module);
    return TRUE;
}
