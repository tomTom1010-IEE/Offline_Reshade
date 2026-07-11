/*
 * Copyright (C) 2021 Patrick Mours
 * SPDX-License-Identifier: BSD-3-Clause
 */

#if defined(RESHADE_API_LIBRARY_EXPORT) && RESHADE_ADDON

#include "reshade.hpp"
#include "addon_manager.hpp"
#include "runtime.hpp"
#include "dll_log.hpp"
#include "ini_file.hpp"
#include <algorithm>
#include <cstring> // std::strlen
#include <fstream>
#include <deque>

namespace
{
	std::string reshade_json_escape(const std::string &value)
	{
		std::string result;
		result.reserve(value.size() + 8);
		for (const char c : value)
		{
			switch (c)
			{
			case '\\': result += "\\\\"; break;
			case '"': result += "\\\""; break;
			case '\n': result += "\\n"; break;
			case '\r': result += "\\r"; break;
			case '\t': result += "\\t"; break;
			default:
				result += static_cast<unsigned char>(c) < 0x20 ? ' ' : c;
				break;
			}
		}
		return result;
	}

	std::string reshade_json_string(const std::string &value)
	{
		return "\"" + reshade_json_escape(value) + "\"";
	}

	const char *offline_event_support(reshade::addon_event event)
	{
		using reshade::addon_event;
		switch (event)
		{
		case addon_event::init_device:
		case addon_event::destroy_device:
		case addon_event::init_command_list:
		case addon_event::destroy_command_list:
		case addon_event::init_command_queue:
		case addon_event::destroy_command_queue:
		case addon_event::init_swapchain:
		case addon_event::destroy_swapchain:
		case addon_event::init_effect_runtime:
		case addon_event::destroy_effect_runtime:
		case addon_event::execute_command_list:
		case addon_event::present:
		case addon_event::finish_present:
		case addon_event::reshade_present:
		case addon_event::reshade_begin_effects:
		case addon_event::reshade_finish_effects:
		case addon_event::reshade_reloaded_effects:
		case addon_event::reshade_set_uniform_value:
		case addon_event::reshade_set_technique_state:
		case addon_event::reshade_overlay:
		case addon_event::reshade_screenshot:
		case addon_event::reshade_render_technique:
		case addon_event::reshade_set_effects_state:
		case addon_event::reshade_set_current_preset_path:
		case addon_event::reshade_reorder_techniques:
		case addon_event::reshade_open_overlay:
			return "supported";
		case addon_event::reshade_overlay_uniform_variable:
		case addon_event::reshade_overlay_technique:
			return "native_overlay_only";
		default:
			return "game_render_stream_unavailable";
		}
	}

	std::vector<std::string> read_addon_log_errors(const std::filesystem::path &log_path)
	{
		std::ifstream log(log_path, std::ios::binary);
		if (!log)
			return {};

		std::deque<std::string> matches;
		std::string line;
		while (std::getline(log, line))
		{
			if (line.find("| ERROR |") == std::string::npos && line.find("| WARN  |") == std::string::npos)
				continue;

			bool related = line.find("add-on") != std::string::npos ||
				line.find("Add-on") != std::string::npos ||
				line.find("AddonInit") != std::string::npos;
			for (const reshade::addon_info &info : reshade::addon_loaded_info)
			{
				if ((!info.name.empty() && line.find(info.name) != std::string::npos) ||
					(!info.file.empty() && line.find(info.file) != std::string::npos))
				{
					related = true;
					break;
				}
			}
			if (!related)
				continue;

			matches.push_back(std::move(line));
			if (matches.size() > 40)
				matches.pop_front();
		}

		return { matches.cbegin(), matches.cend() };
	}
}

void ReShadeLogMessage([[maybe_unused]] void *module, int level, const char *message)
{
#if RESHADE_ADDON
	if (reshade::addon_info *const info = reshade::find_addon(module))
		reshade::log::message(static_cast<reshade::log::level>(level), "[%.*s] %s", static_cast<int>(info->name.size()), info->name.c_str(), message);
	else
#endif
		reshade::log::message(static_cast<reshade::log::level>(level), "%s", message);
}

void ReShadeGetBasePath(char *path, size_t *size)
{
	if (size == nullptr)
		return;

	const std::string path_string = g_reshade_base_path.u8string();

	if (path == nullptr)
	{
		*size = path_string.size() + 1;
	}
	else if (*size != 0)
	{
		*size = path_string.copy(path, *size - 1);
		path[*size] = '\0';
	}
}

bool ReShadeGetConfigValue(void *, reshade::api::effect_runtime *runtime, const char *section, const char *key, char *value, size_t *size)
{
	reshade::ini_file &config = (runtime != nullptr) ? reshade::ini_file::load_cache(static_cast<reshade::runtime *>(runtime)->get_config_path()) : reshade::global_config();

	std::vector<std::string> elements;
	config.get(section != nullptr ? section : std::string(), key != nullptr ? key : std::string(), elements);

	if (size != nullptr || value != nullptr)
	{
		std::string value_string;
		for (const std::string &element : elements)
		{
			value_string += element;
			value_string += '\0';
		}

		if (size != nullptr)
		{
			if (elements.empty())
			{
				*size = 0;
			}
			else if (value == nullptr)
			{
				*size = value_string.size() + 1;
			}
			else if (*size != 0)
			{
				*size = value_string.copy(value, *size - 1);
				value[*size] = '\0';
			}
		}
	}

	return !elements.empty();
}

void ReShadeSetConfigValue(void *module, reshade::api::effect_runtime *runtime, const char *section, const char *key, const char *value)
{
	return ReShadeSetConfigArray(module, runtime, section, key, value, value != nullptr ? std::strlen(value) : 0);
}
void ReShadeSetConfigArray(void *, reshade::api::effect_runtime *runtime, const char *section, const char *key, const char *value, size_t size)
{
	reshade::ini_file &config = (runtime != nullptr) ? reshade::ini_file::load_cache(static_cast<reshade::runtime *>(runtime)->get_config_path()) : reshade::global_config();

	if (value != nullptr)
	{
		std::vector<std::string> elements;
		for (size_t i = 0, k = 0; i < size; i++)
		{
			if (k >= elements.size())
				elements.resize(k + 1);

			if (value[i] == '\0')
			{
				k++;
				continue;
			}

			elements[k] += value[i];
		}

		config.set(section != nullptr ? section : std::string(), key != nullptr ? key : std::string(), elements);

		// Reload configuration after it was modified
		if (runtime != nullptr)
			static_cast<reshade::runtime *>(runtime)->load_config();
	}
	else
	{
		config.remove_key(section != nullptr ? section : std::string(), key != nullptr ? key : std::string());
	}
}

bool ReShadeGetAddonOverlayStateJson(char *value, size_t *size)
{
	if (size == nullptr)
		return false;

	std::string json = "{\"available\":true,\"allLoaded\":";
	json += reshade::addon_all_loaded ? "true" : "false";
	json += ",\"searchPath\":" + reshade_json_string(reshade::addon_search_path.u8string());
	std::filesystem::path log_path = reshade::global_config().path();
	log_path.replace_extension(L".log");
	json += ",\"logPath\":" + reshade_json_string(log_path.u8string());
	json += ",\"logErrors\":[";
	const std::vector<std::string> log_errors = read_addon_log_errors(log_path);
	for (size_t index = 0; index < log_errors.size(); ++index)
	{
		if (index != 0)
			json += ',';
		json += reshade_json_string(log_errors[index]);
	}
	json += ']';
	json += ",\"diagnostics\":[";
	bool first_diagnostic = true;
	for (const reshade::addon_load_diagnostic &diagnostic : reshade::addon_load_diagnostics)
	{
		if (!first_diagnostic)
			json += ',';
		first_diagnostic = false;
		json += "{\"file\":" + reshade_json_string(diagnostic.file);
		json += ",\"path\":" + reshade_json_string(diagnostic.path);
		json += ",\"status\":" + reshade_json_string(diagnostic.status);
		json += ",\"stage\":" + reshade_json_string(diagnostic.stage);
		json += ",\"message\":" + reshade_json_string(diagnostic.message);
		json += ",\"errorCode\":" + std::to_string(diagnostic.error_code);
		json += ",\"dependencyFailure\":" + std::string(diagnostic.dependency_failure ? "true" : "false");
		json += '}';
	}
	json += ']';
	json += ",\"addons\":[";

	bool first_addon = true;
	for (const reshade::addon_info &info : reshade::addon_loaded_info)
	{
		if (!first_addon)
			json += ',';
		first_addon = false;

		json += "{\"name\":" + reshade_json_string(info.name);
		json += ",\"description\":" + reshade_json_string(info.description);
		json += ",\"file\":" + reshade_json_string(info.file);
		json += ",\"author\":" + reshade_json_string(info.author);
		json += ",\"website\":" + reshade_json_string(info.website_url);
		json += ",\"issues\":" + reshade_json_string(info.issues_url);
		json += ",\"apiVersion\":" + std::to_string(info.api_version);
		json += ",\"external\":" + std::string(info.external ? "true" : "false");
		json += ",\"loaded\":" + std::string(info.handle != nullptr ? "true" : "false");
		json += ",\"events\":[";
		bool first_event = true;
		bool has_limited_event = false;
		std::vector<uint32_t> emitted_events;
		for (const std::pair<uint32_t, void *> &registered_event : info.event_callbacks)
		{
			if (std::find(emitted_events.cbegin(), emitted_events.cend(), registered_event.first) != emitted_events.cend())
				continue;
			emitted_events.push_back(registered_event.first);
			const auto event = static_cast<reshade::addon_event>(registered_event.first);
			const char *const support = offline_event_support(event);
			has_limited_event |= std::strcmp(support, "supported") != 0;
			if (!first_event)
				json += ',';
			first_event = false;
			json += "{\"name\":" + reshade_json_string(reshade::addon_event_name(event));
			json += ",\"support\":" + reshade_json_string(support) + '}';
		}
		json += ']';
		json += ",\"offlineCompatibility\":" + reshade_json_string(has_limited_event ? "partial" : "runtime_compatible");
		const bool has_event_overlay = std::any_of(info.event_callbacks.cbegin(), info.event_callbacks.cend(),
			[](const std::pair<uint32_t, void *> &event) { return event.first == static_cast<uint32_t>(reshade::addon_event::reshade_overlay); });
		json += ",\"hasEventOverlay\":" + std::string(has_event_overlay ? "true" : "false");
#if RESHADE_GUI
		json += ",\"hasSettingsOverlay\":" + std::string(info.settings_overlay_callback != nullptr ? "true" : "false");
		json += ",\"overlays\":[";
		bool first_overlay = true;
		for (const reshade::addon_info::overlay_callback &overlay : info.overlay_callbacks)
		{
			if (!first_overlay)
				json += ',';
			first_overlay = false;
			json += reshade_json_string(overlay.title);
		}
		json += "]";
#else
		json += ",\"hasSettingsOverlay\":false,\"overlays\":[]";
#endif
		json += "}";
	}

	json += "]}";

	const size_t required_size = json.size() + 1;
	if (value == nullptr || *size < required_size)
	{
		*size = required_size;
		return value == nullptr;
	}

	std::memcpy(value, json.c_str(), required_size);
	*size = required_size;
	return true;
}

void ReShadeSetExternalAddonOverlay(reshade::api::effect_runtime *runtime, const char *addon_name, const char *overlay_name, bool settings)
{
	if (runtime == nullptr)
		return;

	static_cast<reshade::runtime *>(runtime)->set_external_addon_overlay(addon_name, overlay_name, settings);
}

void ReShadeAddExternalOverlayInputEvent(reshade::api::effect_runtime *runtime, uint32_t message, uintptr_t wparam, intptr_t lparam)
{
	if (runtime == nullptr)
		return;

	static_cast<reshade::runtime *>(runtime)->add_external_overlay_input_event(message, wparam, lparam);
}

void ReShadeNotifyOfflineInputChanged(reshade::api::effect_runtime *runtime, const char *color_path, const char *depth_path, uint32_t width, uint32_t height, uint64_t generation)
{
	if (runtime == nullptr)
		return;

	reshade::ini_file &config = reshade::ini_file::load_cache(static_cast<reshade::runtime *>(runtime)->get_config_path());
	config.set("OFFLINE", "Enabled", true);
	config.set("OFFLINE", "ColorPath", color_path != nullptr ? color_path : "");
	config.set("OFFLINE", "DepthPath", depth_path != nullptr ? depth_path : "");
	config.set("OFFLINE", "InputWidth", width);
	config.set("OFFLINE", "InputHeight", height);
	config.set("OFFLINE", "InputGeneration", generation);
}

bool ReShadeIsEffectRuntimeLoading(reshade::api::effect_runtime *runtime)
{
	return runtime != nullptr && static_cast<reshade::runtime *>(runtime)->is_loading();
}

#include "d3d9/d3d9_impl_device.hpp"
#include "d3d9/d3d9_impl_swapchain.hpp"
#include "d3d10/d3d10_impl_device.hpp"
#include "d3d10/d3d10_impl_swapchain.hpp"
#include "d3d11/d3d11_impl_device.hpp"
#include "d3d11/d3d11_impl_device_context.hpp"
#include "d3d11/d3d11_impl_swapchain.hpp"
#include "d3d12/d3d12_impl_device.hpp"
#include "d3d12/d3d12_impl_command_queue.hpp"
#include "d3d12/d3d12_impl_swapchain.hpp"

bool ReShadeCreateEffectRuntime(reshade::api::device_api api, void *opaque_device, void *opaque_command_queue, void *opaque_swapchain, const char *config_path, reshade::api::effect_runtime **out_runtime)
{
	if (out_runtime == nullptr)
		return false;
	*out_runtime = nullptr;

	if (opaque_swapchain == nullptr || config_path == nullptr)
		return false;

	reshade::api::swapchain *swapchain_impl = nullptr;
	reshade::api::command_queue *graphics_queue_impl = nullptr;

	switch (api)
	{
	case reshade::api::device_api::d3d9:
		{
			com_ptr<IDirect3DDevice9> device;
			com_ptr<IDirect3DSwapChain9> swapchain;
			if (FAILED(static_cast<IUnknown *>(opaque_swapchain)->QueryInterface(&swapchain)))
				return false;
			if (FAILED(swapchain->GetDevice(&device)) || device.get() != opaque_device)
				return false;

			const auto device_impl = new reshade::d3d9::device_impl(device.get());
			swapchain_impl = new reshade::d3d9::swapchain_impl(device_impl, swapchain.get());
			graphics_queue_impl = device_impl;
		}
		break;
	case reshade::api::device_api::d3d10:
		{
			com_ptr<ID3D10Device1> device;
			com_ptr<IDXGISwapChain> swapchain;
			if (FAILED(static_cast<IUnknown *>(opaque_swapchain)->QueryInterface(&swapchain)))
				return false;
			if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&device))) || device.get() != opaque_device)
				return false;

			const auto device_impl = new reshade::d3d10::device_impl(device.get());
			swapchain_impl = new reshade::d3d10::swapchain_impl(device_impl, swapchain.get());
			graphics_queue_impl = device_impl;
		}
		break;
	case reshade::api::device_api::d3d11:
		{
			com_ptr<ID3D11Device> device;
			com_ptr<IDXGISwapChain> swapchain;
			if (FAILED(static_cast<IUnknown *>(opaque_swapchain)->QueryInterface(&swapchain)))
				return false;
			if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&device))) || device.get() != opaque_device)
				return false;

			com_ptr<ID3D11DeviceContext> device_context;
			if (opaque_command_queue != nullptr)
			{
				if (FAILED(static_cast<IUnknown *>(opaque_command_queue)->QueryInterface(&device_context)))
					return false;
			}
			else
			{
				device->GetImmediateContext(&device_context);
			}

			const auto device_impl = new reshade::d3d11::device_impl(device.get());
			swapchain_impl = new reshade::d3d11::swapchain_impl(device_impl, swapchain.get());
			graphics_queue_impl = new reshade::d3d11::device_context_impl(device_impl, device_context.get());
		}
		break;
	case reshade::api::device_api::d3d12:
		{
			com_ptr<ID3D12Device> device;
			com_ptr<IDXGISwapChain3> swapchain;
			if (FAILED(static_cast<IUnknown *>(opaque_swapchain)->QueryInterface(&swapchain)))
				return false;
			if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&device))) || device.get() != opaque_device)
				return false;

			com_ptr<ID3D12CommandQueue> command_queue;
			if (opaque_command_queue == nullptr ||
				FAILED(static_cast<IUnknown *>(opaque_command_queue)->QueryInterface(IID_PPV_ARGS(&command_queue))))
				return false;

			const auto device_impl = new reshade::d3d12::device_impl(device.get());
			swapchain_impl = new reshade::d3d12::swapchain_impl(device_impl, swapchain.get());
			graphics_queue_impl = new reshade::d3d12::command_queue_impl(device_impl, command_queue.get());
		}
		break;
	default:
		return false;
	}

	reshade::load_addons();
	reshade::invoke_addon_event<reshade::addon_event::init_device>(swapchain_impl->get_device());
	if (reshade::api::command_list *const immediate_command_list = graphics_queue_impl->get_immediate_command_list())
		reshade::invoke_addon_event<reshade::addon_event::init_command_list>(immediate_command_list);
	reshade::invoke_addon_event<reshade::addon_event::init_command_queue>(graphics_queue_impl);
	reshade::invoke_addon_event<reshade::addon_event::init_swapchain>(swapchain_impl, false);

	const auto runtime = new reshade::runtime(swapchain_impl, graphics_queue_impl, std::filesystem::u8path(config_path), false);
	if (!runtime->on_init())
	{
		ReShadeDestroyEffectRuntime(runtime);
		return false;
	}

	*out_runtime = runtime;
	return true;
}

void ReShadeDestroyEffectRuntime(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr)
		return;

	reshade::api::device *const device = runtime->get_device();
	reshade::api::swapchain *const swapchain = static_cast<reshade::runtime *>(runtime)->get_swapchain();
	reshade::api::command_queue *const graphics_queue = runtime->get_command_queue();

	static_cast<reshade::runtime *>(runtime)->on_reset();

	delete static_cast<reshade::runtime *>(runtime);

	reshade::invoke_addon_event<reshade::addon_event::destroy_swapchain>(swapchain, false);
	reshade::invoke_addon_event<reshade::addon_event::destroy_command_queue>(graphics_queue);
	if (reshade::api::command_list *const immediate_command_list = graphics_queue->get_immediate_command_list())
		reshade::invoke_addon_event<reshade::addon_event::destroy_command_list>(immediate_command_list);
	reshade::invoke_addon_event<reshade::addon_event::destroy_device>(device);

	reshade::unload_addons();

	switch (device->get_api())
	{
	case reshade::api::device_api::d3d9:
		delete static_cast<reshade::d3d9::swapchain_impl *>(swapchain);
		delete static_cast<reshade::d3d9::device_impl *>(device);
		break;
	case reshade::api::device_api::d3d10:
		delete static_cast<reshade::d3d10::swapchain_impl *>(swapchain);
		delete static_cast<reshade::d3d10::device_impl *>(device);
		break;
	case reshade::api::device_api::d3d11:
		delete static_cast<reshade::d3d11::swapchain_impl *>(swapchain);
		delete static_cast<reshade::d3d11::device_context_impl *>(graphics_queue);
		delete static_cast<reshade::d3d11::device_impl *>(device);
		break;
	case reshade::api::device_api::d3d12:
		delete static_cast<reshade::d3d12::swapchain_impl *>(swapchain);
		delete static_cast<reshade::d3d12::command_queue_impl *>(graphics_queue);
		delete static_cast<reshade::d3d12::device_impl *>(device);
		break;
	}
}


void ReShadeSetExternalOverlayTarget(reshade::api::effect_runtime *runtime, void *render_target_view, uint32_t width, uint32_t height)
{
	if (runtime == nullptr)
		return;

	static_cast<reshade::runtime *>(runtime)->set_external_overlay_target(reshade::api::resource_view { reinterpret_cast<uintptr_t>(render_target_view) }, width, height);
}
void ReShadeBeginPresentEffectRuntime(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr)
		return;

	reshade::api::command_queue *const queue = runtime->get_command_queue();
	reshade::api::swapchain *const swapchain = static_cast<reshade::runtime *>(runtime)->get_swapchain();
	if (reshade::api::command_list *const immediate_command_list = queue->get_immediate_command_list())
		reshade::invoke_addon_event<reshade::addon_event::execute_command_list>(queue, immediate_command_list);
	reshade::invoke_addon_event<reshade::addon_event::present>(queue, swapchain, nullptr, nullptr, 0, nullptr);
}

void ReShadeUpdateAndPresentEffectRuntime(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr)
		return;

	static_cast<reshade::runtime *>(runtime)->on_present();

	runtime->get_command_queue()->flush_immediate_command_list();
}

void ReShadeFinishPresentEffectRuntime(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr)
		return;

	reshade::invoke_addon_event<reshade::addon_event::finish_present>(
		runtime->get_command_queue(),
		static_cast<reshade::runtime *>(runtime)->get_swapchain());
}

#endif

