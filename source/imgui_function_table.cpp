/*
 * Copyright (C) 2024 Patrick Mours
 * SPDX-License-Identifier: BSD-3-Clause OR MIT
 */

#if defined(RESHADE_API_LIBRARY_EXPORT) && RESHADE_GUI && RESHADE_ADDON

#include <algorithm>
#include <cstdarg>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>
#include "dll_log.hpp"
#include "imgui_function_table_19250.hpp"
#include "imgui_function_table_19222.hpp"
#include "imgui_function_table_19191.hpp"
#include "imgui_function_table_19180.hpp"
#include "imgui_function_table_19040.hpp"
#include "imgui_function_table_19000.hpp"
#include "imgui_function_table_18971.hpp"
#include "imgui_function_table_18600.hpp"

extern const imgui_function_table_19250 init_imgui_function_table_19250();
#if RESHADE_ADDON >= 2
extern const imgui_function_table_19222 init_imgui_function_table_19222();
extern const imgui_function_table_19191 init_imgui_function_table_19191();
extern const imgui_function_table_19180 init_imgui_function_table_19180();
extern const imgui_function_table_19040 init_imgui_function_table_19040();
extern const imgui_function_table_19000 init_imgui_function_table_19000();
extern const imgui_function_table_18971 init_imgui_function_table_18971();
extern const imgui_function_table_18600 init_imgui_function_table_18600();
#endif

// Force initialization order (has to happen in a single translation unit to be well defined by declaration order) from newest to oldest, so that older function tables can reference newer ones
const imgui_function_table_19250 g_imgui_function_table_19250 = init_imgui_function_table_19250();
#if RESHADE_ADDON >= 2
const imgui_function_table_19222 g_imgui_function_table_19222 = init_imgui_function_table_19222();
const imgui_function_table_19191 g_imgui_function_table_19191 = init_imgui_function_table_19191();
const imgui_function_table_19180 g_imgui_function_table_19180 = init_imgui_function_table_19180();
const imgui_function_table_19040 g_imgui_function_table_19040 = init_imgui_function_table_19040();
const imgui_function_table_19000 g_imgui_function_table_19000 = init_imgui_function_table_19000();
const imgui_function_table_18971 g_imgui_function_table_18971 = init_imgui_function_table_18971();
const imgui_function_table_18600 g_imgui_function_table_18600 = init_imgui_function_table_18600();
#endif

namespace reshade::imgui_capture
{
	struct widget
	{
		int frame = 0;
		std::string id;
		std::string addon;
		std::string overlay;
		std::string window;
		std::string kind;
		std::string label;
		std::string value;
		std::string minimum;
		std::string maximum;
		std::vector<std::string> items;
		int components = 0;
		bool changed = false;
	};

	static std::mutex s_mutex;
	static bool s_enabled = false;
	static int s_frame = -1;
	static std::vector<widget> s_widgets;
	static std::vector<uint32_t> s_requested_versions;
	static std::vector<std::pair<std::string, std::string>> s_pending_inputs;

	static thread_local bool s_active = false;
	static thread_local std::string s_current_addon;
	static thread_local std::string s_current_overlay;
	static thread_local std::vector<std::string> s_window_stack;
	static thread_local std::vector<std::pair<std::string, uint32_t>> s_widget_occurrences;
	static thread_local std::string s_last_widget_id;
	static thread_local bool s_in_tooltip = false;
	static thread_local std::string s_tooltip_target_id;
	static thread_local std::string s_tooltip_text;
	static thread_local bool s_capture_only = false;
	static thread_local bool s_custom_draw_fallback_recorded = false;
	static thread_local std::vector<std::string> s_forced_open_menus;
	static thread_local std::vector<bool> s_menu_native_stack;

	static std::string json_escape(const std::string &value)
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

	static std::string json_string(const std::string &value)
	{
		return "\"" + json_escape(value) + "\"";
	}

	static std::string string_or_empty(const char *value)
	{
		return value != nullptr ? value : "";
	}

	static std::string make_widget_id(const std::string &kind, const std::string &label)
	{
		const std::string base = s_current_addon + "|" + s_current_overlay + "|" + (s_window_stack.empty() ? s_current_overlay : s_window_stack.back()) + "|" + kind + "|" + label;
		for (auto &occurrence : s_widget_occurrences)
		{
			if (occurrence.first != base)
				continue;

			++occurrence.second;
			return base + "|" + std::to_string(occurrence.second);
		}

		s_widget_occurrences.emplace_back(base, 1);
		return base + "|1";
	}

	static std::string format_float(float value)
	{
		char buffer[64] = {};
		std::snprintf(buffer, sizeof(buffer), "%.6g", value);
		return buffer;
	}

	static std::string format_double(double value)
	{
		char buffer[64] = {};
		std::snprintf(buffer, sizeof(buffer), "%.12g", value);
		return buffer;
	}

	static std::string format_bool(bool value)
	{
		return value ? "true" : "false";
	}

	static std::string format_float_array(const float *values, int components)
	{
		std::string result;
		for (int i = 0; i < components; ++i)
		{
			if (i != 0)
				result += ", ";
			result += format_float(values[i]);
		}
		return result;
	}

	static std::string format_int_array(const int *values, int components)
	{
		std::string result;
		for (int i = 0; i < components; ++i)
		{
			if (i != 0)
				result += ", ";
			result += std::to_string(values[i]);
		}
		return result;
	}

	static std::vector<double> parse_number_values(const std::string &value)
	{
		std::vector<double> result;
		const char *current = value.c_str();
		while (*current != '\0')
		{
			char *end = nullptr;
			const double parsed = std::strtod(current, &end);
			if (end == current)
			{
				++current;
				continue;
			}

			result.push_back(parsed);
			current = end;
		}
		return result;
	}

	static bool parse_bool_value(const std::string &value, bool &result)
	{
		std::string normalized = value;
		std::transform(normalized.begin(), normalized.end(), normalized.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
		if (normalized == "true" || normalized == "1" || normalized == "on" || normalized == "yes")
		{
			result = true;
			return true;
		}
		if (normalized == "false" || normalized == "0" || normalized == "off" || normalized == "no")
		{
			result = false;
			return true;
		}
		return false;
	}

	static std::vector<std::string> parse_nul_separated_items(const char *items)
	{
		std::vector<std::string> result;
		if (items == nullptr)
			return result;

		const char *current = items;
		while (*current != '\0')
		{
			result.emplace_back(current);
			current += result.back().size() + 1;
		}
		return result;
	}

	static std::string format_text_v(const char *fmt, va_list args)
	{
		if (fmt == nullptr)
			return {};

		va_list count_args;
		va_copy(count_args, args);
		const int count = std::vsnprintf(nullptr, 0, fmt, count_args);
		va_end(count_args);
		if (count <= 0)
			return fmt;

		std::vector<char> buffer(static_cast<size_t>(count) + 1);
		va_list print_args;
		va_copy(print_args, args);
		std::vsnprintf(buffer.data(), buffer.size(), fmt, print_args);
		va_end(print_args);
		return std::string(buffer.data(), static_cast<size_t>(count));
	}

	static void ensure_frame_locked()
	{
		const int frame = ImGui::GetFrameCount();
		if (s_frame != frame)
		{
			s_frame = frame;
			s_widgets.clear();
		}
	}

	static bool consume_input(const std::string &id, std::string &value)
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		for (auto it = s_pending_inputs.begin(); it != s_pending_inputs.end(); ++it)
		{
			if (it->first != id)
				continue;

			value = std::move(it->second);
			s_pending_inputs.erase(it);
			return true;
		}
		return false;
	}

	static void add_widget(std::string kind, std::string label, std::string value = {}, std::string minimum = {}, std::string maximum = {}, int components = 0, bool changed = false, std::string id = {}, std::vector<std::string> items = {})
	{
		if (!s_enabled || !s_active)
			return;

		std::lock_guard<std::mutex> lock(s_mutex);
		ensure_frame_locked();
		if (s_widgets.size() >= 4096)
			return;

		widget item;
		item.frame = s_frame;
		item.id = id.empty() ? make_widget_id(kind, label) : std::move(id);
		item.addon = s_current_addon;
		item.overlay = s_current_overlay;
		item.window = s_window_stack.empty() ? s_current_overlay : s_window_stack.back();
		item.kind = std::move(kind);
		item.label = std::move(label);
		item.value = std::move(value);
		item.minimum = std::move(minimum);
		item.maximum = std::move(maximum);
		item.items = std::move(items);
		item.components = components;
		item.changed = changed;
		s_widgets.push_back(std::move(item));
		s_last_widget_id = s_widgets.back().id;
	}

	void set_enabled(bool enabled)
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		s_enabled = enabled;
		if (!enabled)
		{
			s_widgets.clear();
			s_pending_inputs.clear();
			s_frame = -1;
		}
	}

	bool inject_value(const char *id, const char *value)
	{
		if (id == nullptr || *id == '\0' || value == nullptr)
			return false;

		std::lock_guard<std::mutex> lock(s_mutex);
		for (auto &pending : s_pending_inputs)
		{
			if (pending.first == id)
			{
				pending.second = value;
				return true;
			}
		}

		if (s_pending_inputs.size() >= 128)
			s_pending_inputs.erase(s_pending_inputs.begin());
		s_pending_inputs.emplace_back(id, value);
		return true;
	}

	bool is_enabled()
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		return s_enabled;
	}

	void record_requested_version(uint32_t version)
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		if (std::find(s_requested_versions.begin(), s_requested_versions.end(), version) == s_requested_versions.end())
			s_requested_versions.push_back(version);
	}

	void begin_overlay(const char *addon, const char *overlay, bool settings, bool capture_only)
	{
		if (!is_enabled())
			return;

		s_active = true;
		s_current_addon = string_or_empty(addon);
		s_current_overlay = settings ? "Settings" : string_or_empty(overlay);
		s_window_stack.clear();
		s_widget_occurrences.clear();
		s_last_widget_id.clear();
		s_in_tooltip = false;
		s_tooltip_target_id.clear();
		s_tooltip_text.clear();
		s_capture_only = capture_only;
		s_custom_draw_fallback_recorded = false;
		s_menu_native_stack.clear();
		add_widget(settings ? "settings_overlay" : "overlay", s_current_overlay);
	}

	void end_overlay()
	{
		s_active = false;
		s_current_addon.clear();
		s_current_overlay.clear();
		s_window_stack.clear();
		s_widget_occurrences.clear();
		s_last_widget_id.clear();
		s_in_tooltip = false;
		s_tooltip_target_id.clear();
		s_tooltip_text.clear();
		s_capture_only = false;
		s_custom_draw_fallback_recorded = false;
		s_menu_native_stack.clear();
	}

	std::string to_json()
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		std::string json = "{\"available\":true,\"enabled\":";
		json += s_enabled ? "true" : "false";
		json += ",\"frame\":" + std::to_string(s_frame);
		json += ",\"requestedVersions\":[";
		for (size_t i = 0; i < s_requested_versions.size(); ++i)
		{
			if (i != 0)
				json += ',';
			json += std::to_string(s_requested_versions[i]);
		}
		json += "],\"controls\":[";
		for (size_t i = 0; i < s_widgets.size(); ++i)
		{
			if (i != 0)
				json += ',';
			const widget &item = s_widgets[i];
			json += "{\"frame\":" + std::to_string(item.frame);
			json += ",\"id\":" + json_string(item.id);
			json += ",\"addon\":" + json_string(item.addon);
			json += ",\"overlay\":" + json_string(item.overlay);
			json += ",\"window\":" + json_string(item.window);
			json += ",\"kind\":" + json_string(item.kind);
			json += ",\"label\":" + json_string(item.label);
			json += ",\"value\":" + json_string(item.value);
			json += ",\"min\":" + json_string(item.minimum);
			json += ",\"max\":" + json_string(item.maximum);
			json += ",\"items\":[";
			for (size_t item_index = 0; item_index < item.items.size(); ++item_index)
			{
				if (item_index != 0)
					json += ',';
				json += json_string(item.items[item_index]);
			}
			json += "]";
			json += ",\"components\":" + std::to_string(item.components);
			json += ",\"changed\":" + std::string(item.changed ? "true" : "false");
			json += "}";
		}
		json += "]}";
		return json;
	}

	static bool apply_float_input(const std::string &id, float *values, int components)
	{
		if (values == nullptr || components <= 0)
			return false;

		std::string injected;
		if (!consume_input(id, injected))
			return false;

		const auto parsed = parse_number_values(injected);
		if (parsed.empty())
			return false;

		for (int i = 0; i < components; ++i)
			values[i] = static_cast<float>(parsed[std::min<size_t>(static_cast<size_t>(i), parsed.size() - 1)]);
		return true;
	}

	static bool apply_int_input(const std::string &id, int *values, int components)
	{
		if (values == nullptr || components <= 0)
			return false;

		std::string injected;
		if (!consume_input(id, injected))
			return false;

		const auto parsed = parse_number_values(injected);
		if (parsed.empty())
			return false;

		for (int i = 0; i < components; ++i)
			values[i] = static_cast<int>(parsed[std::min<size_t>(static_cast<size_t>(i), parsed.size() - 1)]);
		return true;
	}

	static bool apply_text_input(const std::string &id, char *buffer, size_t buffer_size)
	{
		if (buffer == nullptr || buffer_size == 0)
			return false;

		std::string injected;
		if (!consume_input(id, injected))
			return false;

		const size_t copy_size = std::min(buffer_size - 1, injected.size());
		std::memcpy(buffer, injected.data(), copy_size);
		buffer[copy_size] = '\0';
		return true;
	}

	static bool capture_begin(const char *name, bool *p_open, ImGuiWindowFlags flags)
	{
		const bool result = g_imgui_function_table_19250.Begin(name, p_open, flags);
		if (s_enabled && s_active)
		{
			s_window_stack.push_back(string_or_empty(name));
			add_widget("window", string_or_empty(name), result ? "open" : "closed");
		}
		return result;
	}

	static void capture_end()
	{
		g_imgui_function_table_19250.End();
		if (s_enabled && s_active && !s_window_stack.empty())
			s_window_stack.pop_back();
	}

	static void capture_text_unformatted(const char *text, const char *text_end)
	{
		g_imgui_function_table_19250.TextUnformatted(text, text_end);
		if (text == nullptr)
			return;
		const std::string value = text_end != nullptr ? std::string(text, text_end) : std::string(text);
		if (s_in_tooltip)
		{
			if (!s_tooltip_text.empty())
				s_tooltip_text += '\n';
			s_tooltip_text += value;
		}
		else
			add_widget("text", value);
	}

	static void capture_text_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.TextV(fmt, original_args);
		va_end(original_args);
		const std::string value = format_text_v(fmt, capture_args);
		if (s_in_tooltip)
		{
			if (!s_tooltip_text.empty())
				s_tooltip_text += '\n';
			s_tooltip_text += value;
		}
		else
			add_widget("text", value);
		va_end(capture_args);
	}

	static void capture_text_disabled_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.TextDisabledV(fmt, original_args);
		va_end(original_args);
		const std::string value = format_text_v(fmt, capture_args);
		if (s_in_tooltip)
		{
			if (!s_tooltip_text.empty())
				s_tooltip_text += '\n';
			s_tooltip_text += value;
		}
		else
			add_widget("text", value);
		va_end(capture_args);
	}

	static void capture_text_wrapped_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.TextWrappedV(fmt, original_args);
		va_end(original_args);
		const std::string value = format_text_v(fmt, capture_args);
		if (s_in_tooltip)
		{
			if (!s_tooltip_text.empty())
				s_tooltip_text += '\n';
			s_tooltip_text += value;
		}
		else
			add_widget("text", value);
		va_end(capture_args);
	}

	static bool capture_input_text(const char *label, char *buffer, size_t buffer_size, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback, void *user_data)
	{
		const std::string id = make_widget_id("input_text", string_or_empty(label));
		const bool injected_changed = apply_text_input(id, buffer, buffer_size);
		const bool result = g_imgui_function_table_19250.InputText(label, buffer, buffer_size, flags, callback, user_data);
		add_widget("input_text", string_or_empty(label), buffer != nullptr ? buffer : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_text_multiline(const char *label, char *buffer, size_t buffer_size, const ImVec2 &size, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback, void *user_data)
	{
		const std::string id = make_widget_id("input_text_multiline", string_or_empty(label));
		const bool injected_changed = apply_text_input(id, buffer, buffer_size);
		const bool result = g_imgui_function_table_19250.InputTextMultiline(label, buffer, buffer_size, size, flags, callback, user_data);
		add_widget("input_text_multiline", string_or_empty(label), buffer != nullptr ? buffer : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_text_with_hint(const char *label, const char *hint, char *buffer, size_t buffer_size, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback, void *user_data)
	{
		const std::string id = make_widget_id("input_text", string_or_empty(label));
		const bool injected_changed = apply_text_input(id, buffer, buffer_size);
		const bool result = g_imgui_function_table_19250.InputTextWithHint(label, hint, buffer, buffer_size, flags, callback, user_data);
		add_widget("input_text", string_or_empty(label), buffer != nullptr ? buffer : "", string_or_empty(hint), {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_button(const char *label, const ImVec2 &size)
	{
		const std::string id = make_widget_id("button", string_or_empty(label));
		std::string injected;
		const bool injected_click = consume_input(id, injected);
		const bool result = g_imgui_function_table_19250.Button(label, size);
		add_widget("button", string_or_empty(label), (result || injected_click) ? "clicked" : "", {}, {}, 0, result || injected_click, id);
		return result || injected_click;
	}

	static bool capture_small_button(const char *label)
	{
		const std::string id = make_widget_id("button", string_or_empty(label));
		std::string injected;
		const bool injected_click = consume_input(id, injected);
		const bool result = g_imgui_function_table_19250.SmallButton(label);
		add_widget("button", string_or_empty(label), (result || injected_click) ? "clicked" : "", {}, {}, 0, result || injected_click, id);
		return result || injected_click;
	}

	static bool capture_checkbox(const char *label, bool *v)
	{
		const std::string id = make_widget_id("checkbox", string_or_empty(label));
		std::string injected;
		bool injected_changed = false;
		if (v != nullptr && consume_input(id, injected))
			injected_changed = parse_bool_value(injected, *v);
		const bool result = g_imgui_function_table_19250.Checkbox(label, v);
		add_widget("checkbox", string_or_empty(label), v != nullptr ? format_bool(*v) : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_radio_button(const char *label, bool active)
	{
		const bool result = g_imgui_function_table_19250.RadioButton(label, active);
		add_widget("radio", string_or_empty(label), format_bool(active), {}, {}, 1, result);
		return result;
	}

	static bool capture_radio_button2(const char *label, int *v, int v_button)
	{
		const bool result = g_imgui_function_table_19250.RadioButton2(label, v, v_button);
		add_widget("radio", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", {}, {}, 1, result);
		return result;
	}

	static bool capture_begin_combo(const char *label, const char *preview_value, ImGuiComboFlags flags)
	{
		const bool result = g_imgui_function_table_19250.BeginCombo(label, preview_value, flags);
		add_widget("combo_begin", string_or_empty(label), string_or_empty(preview_value), {}, {}, 1, result);
		return result;
	}

	static bool capture_combo(const char *label, int *current_item, const char *const items[], int items_count, int popup_max_height_in_items)
	{
		const std::string id = make_widget_id("combo", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, current_item, 1);
		const bool result = g_imgui_function_table_19250.Combo(label, current_item, items, items_count, popup_max_height_in_items);
		std::string value = current_item != nullptr ? std::to_string(*current_item) : "";
		if (current_item != nullptr && items != nullptr && *current_item >= 0 && *current_item < items_count && items[*current_item] != nullptr)
			value += " (" + std::string(items[*current_item]) + ")";
		std::vector<std::string> item_list;
		if (items != nullptr)
		{
			for (int i = 0; i < items_count; ++i)
				item_list.emplace_back(items[i] != nullptr ? items[i] : "");
		}
		add_widget("combo", string_or_empty(label), value, "0", std::to_string(std::max(0, items_count - 1)), 1, result || injected_changed, id, std::move(item_list));
		return result || injected_changed;
	}

	static bool capture_combo2(const char *label, int *current_item, const char *items_separated_by_zeros, int popup_max_height_in_items)
	{
		const std::string id = make_widget_id("combo", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, current_item, 1);
		const bool result = g_imgui_function_table_19250.Combo2(label, current_item, items_separated_by_zeros, popup_max_height_in_items);
		auto item_list = parse_nul_separated_items(items_separated_by_zeros);
		add_widget("combo", string_or_empty(label), current_item != nullptr ? std::to_string(*current_item) : "", "0", item_list.empty() ? "" : std::to_string(item_list.size() - 1), 1, result || injected_changed, id, std::move(item_list));
		return result || injected_changed;
	}

	static bool capture_drag_float(const char *label, float *v, float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.DragFloat(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", format_float(v_min), format_float(v_max), 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_float2(const char *label, float v[2], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.DragFloat2(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", format_float(v_min), format_float(v_max), 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_float3(const char *label, float v[3], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.DragFloat3(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", format_float(v_min), format_float(v_max), 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_float4(const char *label, float v[4], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.DragFloat4(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", format_float(v_min), format_float(v_max), 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_int(const char *label, int *v, float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.DragInt(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", std::to_string(v_min), std::to_string(v_max), 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_int2(const char *label, int v[2], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.DragInt2(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", std::to_string(v_min), std::to_string(v_max), 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_int3(const char *label, int v[3], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.DragInt3(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", std::to_string(v_min), std::to_string(v_max), 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_drag_int4(const char *label, int v[4], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("drag_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.DragInt4(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", std::to_string(v_min), std::to_string(v_max), 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_float(const char *label, float *v, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.SliderFloat(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", format_float(v_min), format_float(v_max), 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_float2(const char *label, float v[2], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.SliderFloat2(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", format_float(v_min), format_float(v_max), 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_float3(const char *label, float v[3], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.SliderFloat3(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", format_float(v_min), format_float(v_max), 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_float4(const char *label, float v[4], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.SliderFloat4(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", format_float(v_min), format_float(v_max), 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_int(const char *label, int *v, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.SliderInt(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", std::to_string(v_min), std::to_string(v_max), 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_int2(const char *label, int v[2], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.SliderInt2(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", std::to_string(v_min), std::to_string(v_max), 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_int3(const char *label, int v[3], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.SliderInt3(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", std::to_string(v_min), std::to_string(v_max), 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_slider_int4(const char *label, int v[4], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const std::string id = make_widget_id("slider_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.SliderInt4(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", std::to_string(v_min), std::to_string(v_max), 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_float(const char *label, float *v, float step, float step_fast, const char *format, ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.InputFloat(label, v, step, step_fast, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_float2(const char *label, float v[2], const char *format, ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.InputFloat2(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", {}, {}, 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_float3(const char *label, float v[3], const char *format, ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.InputFloat3(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", {}, {}, 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_float4(const char *label, float v[4], const char *format, ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_float", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.InputFloat4(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", {}, {}, 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_int(const char *label, int *v, int step, int step_fast, ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 1);
		const bool result = g_imgui_function_table_19250.InputInt(label, v, step, step_fast, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_int2(const char *label, int v[2], ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 2);
		const bool result = g_imgui_function_table_19250.InputInt2(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", {}, {}, 2, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_int3(const char *label, int v[3], ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 3);
		const bool result = g_imgui_function_table_19250.InputInt3(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", {}, {}, 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_input_int4(const char *label, int v[4], ImGuiInputTextFlags flags)
	{
		const std::string id = make_widget_id("input_int", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, v, 4);
		const bool result = g_imgui_function_table_19250.InputInt4(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", {}, {}, 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_color_edit3(const char *label, float col[3], ImGuiColorEditFlags flags)
	{
		const std::string id = make_widget_id("color", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, col, 3);
		const bool result = g_imgui_function_table_19250.ColorEdit3(label, col, flags);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 3) : "", {}, {}, 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_color_edit4(const char *label, float col[4], ImGuiColorEditFlags flags)
	{
		const std::string id = make_widget_id("color", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, col, 4);
		const bool result = g_imgui_function_table_19250.ColorEdit4(label, col, flags);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 4) : "", {}, {}, 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_color_picker3(const char *label, float col[3], ImGuiColorEditFlags flags)
	{
		const std::string id = make_widget_id("color", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, col, 3);
		const bool result = g_imgui_function_table_19250.ColorPicker3(label, col, flags);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 3) : "", {}, {}, 3, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_color_picker4(const char *label, float col[4], ImGuiColorEditFlags flags, const float *ref_col)
	{
		const std::string id = make_widget_id("color", string_or_empty(label));
		const bool injected_changed = apply_float_input(id, col, 4);
		const bool result = g_imgui_function_table_19250.ColorPicker4(label, col, flags, ref_col);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 4) : "", {}, {}, 4, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_tree_node(const char *label)
	{
		const std::string id = make_widget_id("tree_node", string_or_empty(label));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
			g_imgui_function_table_19250.SetNextItemOpen(injected_open, ImGuiCond_Always);
		const bool result = g_imgui_function_table_19250.TreeNode(label);
		add_widget("tree_node", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static bool capture_tree_node_ex(const char *label, ImGuiTreeNodeFlags flags)
	{
		const std::string id = make_widget_id("tree_node", string_or_empty(label));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
			g_imgui_function_table_19250.SetNextItemOpen(injected_open, ImGuiCond_Always);
		const bool result = g_imgui_function_table_19250.TreeNodeEx(label, flags);
		add_widget("tree_node", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static void capture_tree_pop()
	{
		g_imgui_function_table_19250.TreePop();
		add_widget("tree_end", "");
	}

	static bool capture_collapsing_header(const char *label, ImGuiTreeNodeFlags flags)
	{
		const std::string id = make_widget_id("collapsing_header", string_or_empty(label));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
			g_imgui_function_table_19250.SetNextItemOpen(injected_open, ImGuiCond_Always);
		const bool result = g_imgui_function_table_19250.CollapsingHeader(label, flags);
		add_widget("collapsing_header", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static bool capture_collapsing_header2(const char *label, bool *visible, ImGuiTreeNodeFlags flags)
	{
		const std::string id = make_widget_id("collapsing_header", string_or_empty(label));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
			g_imgui_function_table_19250.SetNextItemOpen(injected_open, ImGuiCond_Always);
		const bool result = g_imgui_function_table_19250.CollapsingHeader2(label, visible, flags);
		add_widget("collapsing_header", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static bool capture_begin_tab_bar(const char *id_value, ImGuiTabBarFlags flags)
	{
		const bool result = g_imgui_function_table_19250.BeginTabBar(id_value, flags);
		add_widget("tab_bar_begin", string_or_empty(id_value), result ? "open" : "closed");
		return result;
	}

	static void capture_end_tab_bar()
	{
		g_imgui_function_table_19250.EndTabBar();
		add_widget("tab_bar_end", "");
	}

	static bool capture_begin_tab_item(const char *label, bool *open, ImGuiTabItemFlags flags)
	{
		const std::string id = make_widget_id("tab_item", string_or_empty(label));
		std::string injected;
		bool injected_selected = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_selected) && injected_selected)
			flags |= ImGuiTabItemFlags_SetSelected;
		const bool result = g_imgui_function_table_19250.BeginTabItem(label, open, flags);
		add_widget("tab_item_begin", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static void capture_end_tab_item()
	{
		g_imgui_function_table_19250.EndTabItem();
		add_widget("tab_item_end", "");
	}

	static bool capture_begin_tooltip()
	{
		const bool result = g_imgui_function_table_19250.BeginTooltip();
		if (result && s_enabled && s_active)
		{
			s_in_tooltip = true;
			s_tooltip_target_id = s_last_widget_id;
			s_tooltip_text.clear();
		}
		return result;
	}

	static void capture_end_tooltip()
	{
		g_imgui_function_table_19250.EndTooltip();
		if (s_in_tooltip)
			add_widget("tooltip", s_tooltip_text, s_tooltip_target_id);
		s_in_tooltip = false;
		s_tooltip_target_id.clear();
		s_tooltip_text.clear();
	}

	static void capture_set_tooltip_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.SetTooltipV(fmt, original_args);
		va_end(original_args);
		add_widget("tooltip", format_text_v(fmt, capture_args), s_last_widget_id);
		va_end(capture_args);
	}

	static bool capture_begin_item_tooltip()
	{
		bool result = g_imgui_function_table_19250.BeginItemTooltip();
		if (!result && s_capture_only)
			result = g_imgui_function_table_19250.BeginTooltip();
		if (result && s_enabled && s_active)
		{
			s_in_tooltip = true;
			s_tooltip_target_id = s_last_widget_id;
			s_tooltip_text.clear();
		}
		return result;
	}

	static void capture_set_item_tooltip_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.SetItemTooltipV(fmt, original_args);
		va_end(original_args);
		add_widget("tooltip", format_text_v(fmt, capture_args), s_last_widget_id);
		va_end(capture_args);
	}

	static bool capture_begin_menu(const char *label, bool enabled)
	{
		const std::string id = make_widget_id("menu", string_or_empty(label));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
		{
			const auto existing = std::find(s_forced_open_menus.begin(), s_forced_open_menus.end(), id);
			if (injected_open && existing == s_forced_open_menus.end())
				s_forced_open_menus.push_back(id);
			else if (!injected_open && existing != s_forced_open_menus.end())
				s_forced_open_menus.erase(existing);
		}

		const bool native_result = g_imgui_function_table_19250.BeginMenu(label, enabled);
		const bool forced_result = enabled && !native_result &&
			std::find(s_forced_open_menus.cbegin(), s_forced_open_menus.cend(), id) != s_forced_open_menus.cend();
		const bool result = native_result || forced_result;
		if (result)
			s_menu_native_stack.push_back(native_result);
		add_widget("menu_begin", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static void capture_end_menu()
	{
		const bool native_menu = !s_menu_native_stack.empty() && s_menu_native_stack.back();
		if (!s_menu_native_stack.empty())
			s_menu_native_stack.pop_back();
		if (native_menu)
			g_imgui_function_table_19250.EndMenu();
		add_widget("menu_end", "");
	}

	static bool capture_begin_popup(const char *popup_id, ImGuiWindowFlags flags)
	{
		const std::string id = make_widget_id("popup", string_or_empty(popup_id));
		std::string injected;
		bool injected_open = false;
		const bool has_injected_state = consume_input(id, injected) && parse_bool_value(injected, injected_open);
		if (has_injected_state && injected_open)
			g_imgui_function_table_19250.OpenPopup(popup_id, ImGuiPopupFlags_None);
		const bool result = g_imgui_function_table_19250.BeginPopup(popup_id, flags);
		if (result && has_injected_state && !injected_open)
			g_imgui_function_table_19250.CloseCurrentPopup();
		add_widget("popup_begin", string_or_empty(popup_id), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static bool capture_begin_popup_modal(const char *name, bool *open, ImGuiWindowFlags flags)
	{
		const std::string id = make_widget_id("popup_modal", string_or_empty(name));
		std::string injected;
		bool injected_open = false;
		if (consume_input(id, injected) && parse_bool_value(injected, injected_open))
		{
			if (injected_open)
				g_imgui_function_table_19250.OpenPopup(name, ImGuiPopupFlags_None);
			else if (open != nullptr)
				*open = false;
		}
		const bool result = g_imgui_function_table_19250.BeginPopupModal(name, open, flags);
		add_widget("popup_modal_begin", string_or_empty(name), result ? "open" : "closed", {}, {}, 0, false, id);
		return result;
	}

	static void capture_end_popup()
	{
		g_imgui_function_table_19250.EndPopup();
		add_widget("popup_end", "");
	}

	static bool capture_selectable(const char *label, bool selected, ImGuiSelectableFlags flags, const ImVec2 &size)
	{
		const std::string id = make_widget_id("selectable", string_or_empty(label));
		std::string injected;
		const bool injected_click = consume_input(id, injected);
		const bool result = g_imgui_function_table_19250.Selectable(label, selected, flags, size);
		add_widget("selectable", string_or_empty(label), format_bool(selected), {}, {}, 1, result || injected_click, id);
		return result || injected_click;
	}

	static bool capture_selectable2(const char *label, bool *selected, ImGuiSelectableFlags flags, const ImVec2 &size)
	{
		const std::string id = make_widget_id("selectable", string_or_empty(label));
		std::string injected;
		bool injected_changed = false;
		if (selected != nullptr && consume_input(id, injected))
			injected_changed = parse_bool_value(injected, *selected);
		const bool result = g_imgui_function_table_19250.Selectable2(label, selected, flags, size);
		add_widget("selectable", string_or_empty(label), selected != nullptr ? format_bool(*selected) : "", {}, {}, 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static bool capture_list_box(const char *label, int *current_item, const char *const items[], int items_count, int height_in_items)
	{
		const std::string id = make_widget_id("list_box", string_or_empty(label));
		const bool injected_changed = apply_int_input(id, current_item, 1);
		const bool result = g_imgui_function_table_19250.ListBox(label, current_item, items, items_count, height_in_items);
		std::vector<std::string> item_list;
		if (items != nullptr)
		{
			for (int i = 0; i < items_count; ++i)
				item_list.emplace_back(items[i] != nullptr ? items[i] : "");
		}
		add_widget("list_box", string_or_empty(label), current_item != nullptr ? std::to_string(*current_item) : "", "0", std::to_string(std::max(0, items_count - 1)), 1, result || injected_changed, id, std::move(item_list));
		return result || injected_changed;
	}

	static bool capture_menu_item(const char *label, const char *shortcut, bool selected, bool enabled)
	{
		const std::string id = make_widget_id("menu_item", string_or_empty(label));
		std::string injected;
		const bool injected_click = enabled && consume_input(id, injected);
		const bool result = g_imgui_function_table_19250.MenuItem(label, shortcut, selected, enabled);
		add_widget("menu_item", string_or_empty(label), format_bool(selected), string_or_empty(shortcut), enabled ? "enabled" : "disabled", 1, result || injected_click, id);
		return result || injected_click;
	}

	static bool capture_menu_item2(const char *label, const char *shortcut, bool *selected, bool enabled)
	{
		const std::string id = make_widget_id("menu_item", string_or_empty(label));
		std::string injected;
		bool injected_changed = false;
		if (enabled && selected != nullptr && consume_input(id, injected))
			injected_changed = parse_bool_value(injected, *selected);
		const bool result = g_imgui_function_table_19250.MenuItem2(label, shortcut, selected, enabled);
		add_widget("menu_item", string_or_empty(label), selected != nullptr ? format_bool(*selected) : "", string_or_empty(shortcut), enabled ? "enabled" : "disabled", 1, result || injected_changed, id);
		return result || injected_changed;
	}

	static void capture_image(ImTextureRef texture, const ImVec2 &size, const ImVec2 &uv0, const ImVec2 &uv1)
	{
		g_imgui_function_table_19250.Image(texture, size, uv0, uv1);
		add_widget("native_fallback", "Texture or image preview", "Requires native ImGui rendering");
	}

	static void capture_image_19040(ImTextureID texture, const ImVec2 &size, const ImVec2 &uv0, const ImVec2 &uv1, const ImVec4 &tint, const ImVec4 &border)
	{
		g_imgui_function_table_19040.Image(texture, size, uv0, uv1, tint, border);
		add_widget("native_fallback", "Texture or image preview", "Requires native ImGui rendering");
	}

	static void capture_plot_lines(const char *label, const float *values, int values_count, int values_offset, const char *overlay_text, float scale_min, float scale_max, ImVec2 graph_size, int stride)
	{
		g_imgui_function_table_19250.PlotLines(label, values, values_count, values_offset, overlay_text, scale_min, scale_max, graph_size, stride);
		add_widget("native_fallback", string_or_empty(label), "Plot requires native ImGui rendering");
	}

	static void capture_plot_histogram(const char *label, const float *values, int values_count, int values_offset, const char *overlay_text, float scale_min, float scale_max, ImVec2 graph_size, int stride)
	{
		g_imgui_function_table_19250.PlotHistogram(label, values, values_count, values_offset, overlay_text, scale_min, scale_max, graph_size, stride);
		add_widget("native_fallback", string_or_empty(label), "Plot requires native ImGui rendering");
	}

	static void record_custom_draw_fallback()
	{
		if (!s_active || s_custom_draw_fallback_recorded)
			return;
		s_custom_draw_fallback_recorded = true;
		add_widget("native_fallback", "Custom drawing", "Custom ImGui draw-list commands require native rendering");
	}

	static imgui_draw_list_19250 *capture_get_window_draw_list()
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19250.GetWindowDrawList();
	}
	static imgui_draw_list_19250 *capture_get_background_draw_list(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19250.GetBackgroundDrawList(viewport);
	}
	static imgui_draw_list_19250 *capture_get_foreground_draw_list(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19250.GetForegroundDrawList(viewport);
	}

#if RESHADE_ADDON >= 2
	static imgui_draw_list_19222 *capture_get_window_draw_list_19222()
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19222.GetWindowDrawList();
	}
	static imgui_draw_list_19222 *capture_get_background_draw_list_19222(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19222.GetBackgroundDrawList(viewport);
	}
	static imgui_draw_list_19222 *capture_get_foreground_draw_list_19222(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19222.GetForegroundDrawList(viewport);
	}

	static imgui_draw_list_19040 *capture_get_window_draw_list_19040()
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19040.GetWindowDrawList();
	}
	static imgui_draw_list_19040 *capture_get_background_draw_list_19040()
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19040.GetBackgroundDrawList();
	}
	static imgui_draw_list_19040 *capture_get_foreground_draw_list_19040()
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19040.GetForegroundDrawList();
	}
	static imgui_draw_list_19040 *capture_get_background_draw_list2_19040(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19040.GetBackgroundDrawList2(viewport);
	}
	static imgui_draw_list_19040 *capture_get_foreground_draw_list2_19040(ImGuiViewport *viewport)
	{
		record_custom_draw_fallback();
		return g_imgui_function_table_19040.GetForegroundDrawList2(viewport);
	}
#endif

	static const imgui_function_table_19250 &proxy_table_19250()
	{
		static const imgui_function_table_19250 table = [] {
			imgui_function_table_19250 proxy = g_imgui_function_table_19250;
			proxy.Begin = capture_begin;
			proxy.End = capture_end;
			proxy.TextUnformatted = capture_text_unformatted;
			proxy.TextV = capture_text_v;
			proxy.TextDisabledV = capture_text_disabled_v;
			proxy.TextWrappedV = capture_text_wrapped_v;
			proxy.Image = capture_image;
			proxy.GetWindowDrawList = capture_get_window_draw_list;
			proxy.GetBackgroundDrawList = capture_get_background_draw_list;
			proxy.GetForegroundDrawList = capture_get_foreground_draw_list;
			proxy.Button = capture_button;
			proxy.SmallButton = capture_small_button;
			proxy.Checkbox = capture_checkbox;
			proxy.RadioButton = capture_radio_button;
			proxy.RadioButton2 = capture_radio_button2;
			proxy.BeginCombo = capture_begin_combo;
			proxy.Combo = capture_combo;
			proxy.Combo2 = capture_combo2;
			proxy.InputText = capture_input_text;
			proxy.InputTextMultiline = capture_input_text_multiline;
			proxy.InputTextWithHint = capture_input_text_with_hint;
			proxy.DragFloat = capture_drag_float;
			proxy.DragFloat2 = capture_drag_float2;
			proxy.DragFloat3 = capture_drag_float3;
			proxy.DragFloat4 = capture_drag_float4;
			proxy.DragInt = capture_drag_int;
			proxy.DragInt2 = capture_drag_int2;
			proxy.DragInt3 = capture_drag_int3;
			proxy.DragInt4 = capture_drag_int4;
			proxy.SliderFloat = capture_slider_float;
			proxy.SliderFloat2 = capture_slider_float2;
			proxy.SliderFloat3 = capture_slider_float3;
			proxy.SliderFloat4 = capture_slider_float4;
			proxy.SliderInt = capture_slider_int;
			proxy.SliderInt2 = capture_slider_int2;
			proxy.SliderInt3 = capture_slider_int3;
			proxy.SliderInt4 = capture_slider_int4;
			proxy.InputFloat = capture_input_float;
			proxy.InputFloat2 = capture_input_float2;
			proxy.InputFloat3 = capture_input_float3;
			proxy.InputFloat4 = capture_input_float4;
			proxy.InputInt = capture_input_int;
			proxy.InputInt2 = capture_input_int2;
			proxy.InputInt3 = capture_input_int3;
			proxy.InputInt4 = capture_input_int4;
			proxy.ColorEdit3 = capture_color_edit3;
			proxy.ColorEdit4 = capture_color_edit4;
			proxy.ColorPicker3 = capture_color_picker3;
			proxy.ColorPicker4 = capture_color_picker4;
			proxy.TreeNode = capture_tree_node;
			proxy.TreeNodeEx = capture_tree_node_ex;
			proxy.TreePop = capture_tree_pop;
			proxy.CollapsingHeader = capture_collapsing_header;
			proxy.CollapsingHeader2 = capture_collapsing_header2;
			proxy.Selectable = capture_selectable;
			proxy.Selectable2 = capture_selectable2;
			proxy.ListBox = capture_list_box;
			proxy.MenuItem = capture_menu_item;
			proxy.MenuItem2 = capture_menu_item2;
			proxy.BeginMenu = capture_begin_menu;
			proxy.EndMenu = capture_end_menu;
			proxy.BeginTooltip = capture_begin_tooltip;
			proxy.BeginItemTooltip = capture_begin_item_tooltip;
			proxy.EndTooltip = capture_end_tooltip;
			proxy.SetTooltipV = capture_set_tooltip_v;
			proxy.SetItemTooltipV = capture_set_item_tooltip_v;
			proxy.BeginPopup = capture_begin_popup;
			proxy.BeginPopupModal = capture_begin_popup_modal;
			proxy.EndPopup = capture_end_popup;
			proxy.BeginTabBar = capture_begin_tab_bar;
			proxy.EndTabBar = capture_end_tab_bar;
			proxy.BeginTabItem = capture_begin_tab_item;
			proxy.EndTabItem = capture_end_tab_item;
			proxy.PlotLines = capture_plot_lines;
			proxy.PlotHistogram = capture_plot_histogram;
			return proxy;
		}();
		return table;
	}

#if RESHADE_ADDON >= 2
	template <typename Table>
	static void assign_common_capture_functions(Table &proxy)
	{
		proxy.Begin = capture_begin;
		proxy.End = capture_end;
		proxy.TextUnformatted = capture_text_unformatted;
		proxy.TextV = capture_text_v;
		proxy.TextDisabledV = capture_text_disabled_v;
		proxy.TextWrappedV = capture_text_wrapped_v;
		proxy.Button = capture_button;
		proxy.SmallButton = capture_small_button;
		proxy.Checkbox = capture_checkbox;
		proxy.RadioButton = capture_radio_button;
		proxy.RadioButton2 = capture_radio_button2;
		proxy.BeginCombo = capture_begin_combo;
		proxy.Combo = capture_combo;
		proxy.Combo2 = capture_combo2;
		proxy.InputText = capture_input_text;
		proxy.InputTextMultiline = capture_input_text_multiline;
		proxy.InputTextWithHint = capture_input_text_with_hint;
		proxy.DragFloat = capture_drag_float;
		proxy.DragFloat2 = capture_drag_float2;
		proxy.DragFloat3 = capture_drag_float3;
		proxy.DragFloat4 = capture_drag_float4;
		proxy.DragInt = capture_drag_int;
		proxy.DragInt2 = capture_drag_int2;
		proxy.DragInt3 = capture_drag_int3;
		proxy.DragInt4 = capture_drag_int4;
		proxy.SliderFloat = capture_slider_float;
		proxy.SliderFloat2 = capture_slider_float2;
		proxy.SliderFloat3 = capture_slider_float3;
		proxy.SliderFloat4 = capture_slider_float4;
		proxy.SliderInt = capture_slider_int;
		proxy.SliderInt2 = capture_slider_int2;
		proxy.SliderInt3 = capture_slider_int3;
		proxy.SliderInt4 = capture_slider_int4;
		proxy.InputFloat = capture_input_float;
		proxy.InputFloat2 = capture_input_float2;
		proxy.InputFloat3 = capture_input_float3;
		proxy.InputFloat4 = capture_input_float4;
		proxy.InputInt = capture_input_int;
		proxy.InputInt2 = capture_input_int2;
		proxy.InputInt3 = capture_input_int3;
		proxy.InputInt4 = capture_input_int4;
		proxy.ColorEdit3 = capture_color_edit3;
		proxy.ColorEdit4 = capture_color_edit4;
		proxy.ColorPicker3 = capture_color_picker3;
		proxy.ColorPicker4 = capture_color_picker4;
		proxy.TreeNode = capture_tree_node;
		proxy.TreeNodeEx = capture_tree_node_ex;
		proxy.TreePop = capture_tree_pop;
		proxy.CollapsingHeader = capture_collapsing_header;
		proxy.CollapsingHeader2 = capture_collapsing_header2;
		proxy.Selectable = capture_selectable;
		proxy.Selectable2 = capture_selectable2;
		proxy.ListBox = capture_list_box;
		proxy.MenuItem = capture_menu_item;
		proxy.MenuItem2 = capture_menu_item2;
		proxy.BeginMenu = capture_begin_menu;
		proxy.EndMenu = capture_end_menu;
		proxy.BeginTooltip = capture_begin_tooltip;
		proxy.BeginItemTooltip = capture_begin_item_tooltip;
		proxy.EndTooltip = capture_end_tooltip;
		proxy.SetTooltipV = capture_set_tooltip_v;
		proxy.SetItemTooltipV = capture_set_item_tooltip_v;
		proxy.BeginPopup = capture_begin_popup;
		proxy.BeginPopupModal = capture_begin_popup_modal;
		proxy.EndPopup = capture_end_popup;
		proxy.BeginTabBar = capture_begin_tab_bar;
		proxy.EndTabBar = capture_end_tab_bar;
		proxy.BeginTabItem = capture_begin_tab_item;
		proxy.EndTabItem = capture_end_tab_item;
		proxy.PlotLines = capture_plot_lines;
		proxy.PlotHistogram = capture_plot_histogram;
	}

	static const imgui_function_table_19222 &proxy_table_19222()
	{
		static const imgui_function_table_19222 table = [] {
			imgui_function_table_19222 proxy = g_imgui_function_table_19222;
			assign_common_capture_functions(proxy);
			proxy.Image = capture_image;
			proxy.GetWindowDrawList = capture_get_window_draw_list_19222;
			proxy.GetBackgroundDrawList = capture_get_background_draw_list_19222;
			proxy.GetForegroundDrawList = capture_get_foreground_draw_list_19222;
			return proxy;
		}();
		return table;
	}

	static const imgui_function_table_19040 &proxy_table_19040()
	{
		static const imgui_function_table_19040 table = [] {
			imgui_function_table_19040 proxy = g_imgui_function_table_19040;
			assign_common_capture_functions(proxy);
			proxy.Image = capture_image_19040;
			proxy.GetWindowDrawList = capture_get_window_draw_list_19040;
			proxy.GetBackgroundDrawList = capture_get_background_draw_list_19040;
			proxy.GetForegroundDrawList = capture_get_foreground_draw_list_19040;
			proxy.GetBackgroundDrawList2 = capture_get_background_draw_list2_19040;
			proxy.GetForegroundDrawList2 = capture_get_foreground_draw_list2_19040;
			return proxy;
		}();
		return table;
	}
#endif
}

extern "C" __declspec(dllexport) void ReShadeSetAddonImGuiCaptureEnabled(bool enabled)
{
	reshade::imgui_capture::set_enabled(enabled);
}

extern "C" __declspec(dllexport) bool ReShadeGetAddonImGuiCaptureJson(char *value, size_t *size)
{
	if (size == nullptr)
		return false;

	const std::string json = reshade::imgui_capture::to_json();
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

extern "C" __declspec(dllexport) bool ReShadeInjectAddonImGuiValue(const char *id, const char *value)
{
	return reshade::imgui_capture::inject_value(id, value);
}

extern "C" __declspec(dllexport) const void *ReShadeGetImGuiFunctionTable(uint32_t version)
{
	reshade::imgui_capture::record_requested_version(version);

	if (version == 19250)
		return &reshade::imgui_capture::proxy_table_19250();
#if RESHADE_ADDON >= 2
	if (version == 19220 || version == 19222)
		return &reshade::imgui_capture::proxy_table_19222();
	if (version == 19190 || version == 19191)
		return &g_imgui_function_table_19191;
	if (version == 19180)
		return &g_imgui_function_table_19180;
	if (version == 19040)
		return &reshade::imgui_capture::proxy_table_19040();
	if (version >= 19000 && version < 19040)
		return &g_imgui_function_table_19000;
	if (version == 18971)
		return &g_imgui_function_table_18971;
	if (version == 18600)
		return &g_imgui_function_table_18600;
#endif

	reshade::log::message(reshade::log::level::error, "Failed to retrieve ImGui function table, because the requested ImGui version (%u) is not supported.", version);
	return nullptr;
}

#endif
