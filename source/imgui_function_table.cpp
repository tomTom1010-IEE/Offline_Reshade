/*
 * Copyright (C) 2024 Patrick Mours
 * SPDX-License-Identifier: BSD-3-Clause OR MIT
 */

#if defined(RESHADE_API_LIBRARY_EXPORT) && RESHADE_GUI && RESHADE_ADDON

#include <algorithm>
#include <cstdarg>
#include <cstdio>
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
		std::string addon;
		std::string overlay;
		std::string window;
		std::string kind;
		std::string label;
		std::string value;
		std::string minimum;
		std::string maximum;
		int components = 0;
		bool changed = false;
	};

	static std::mutex s_mutex;
	static bool s_enabled = false;
	static int s_frame = -1;
	static std::vector<widget> s_widgets;
	static std::vector<uint32_t> s_requested_versions;

	static thread_local bool s_active = false;
	static thread_local std::string s_current_addon;
	static thread_local std::string s_current_overlay;
	static thread_local std::vector<std::string> s_window_stack;

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

	static void add_widget(std::string kind, std::string label, std::string value = {}, std::string minimum = {}, std::string maximum = {}, int components = 0, bool changed = false)
	{
		if (!s_enabled || !s_active)
			return;

		std::lock_guard<std::mutex> lock(s_mutex);
		ensure_frame_locked();
		if (s_widgets.size() >= 512)
			return;

		widget item;
		item.frame = s_frame;
		item.addon = s_current_addon;
		item.overlay = s_current_overlay;
		item.window = s_window_stack.empty() ? s_current_overlay : s_window_stack.back();
		item.kind = std::move(kind);
		item.label = std::move(label);
		item.value = std::move(value);
		item.minimum = std::move(minimum);
		item.maximum = std::move(maximum);
		item.components = components;
		item.changed = changed;
		s_widgets.push_back(std::move(item));
	}

	void set_enabled(bool enabled)
	{
		std::lock_guard<std::mutex> lock(s_mutex);
		s_enabled = enabled;
		if (!enabled)
		{
			s_widgets.clear();
			s_frame = -1;
		}
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

	void begin_overlay(const char *addon, const char *overlay, bool settings)
	{
		if (!is_enabled())
			return;

		s_active = true;
		s_current_addon = string_or_empty(addon);
		s_current_overlay = settings ? "Settings" : string_or_empty(overlay);
		s_window_stack.clear();
		add_widget(settings ? "settings_overlay" : "overlay", s_current_overlay);
	}

	void end_overlay()
	{
		s_active = false;
		s_current_addon.clear();
		s_current_overlay.clear();
		s_window_stack.clear();
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
			json += ",\"addon\":" + json_string(item.addon);
			json += ",\"overlay\":" + json_string(item.overlay);
			json += ",\"window\":" + json_string(item.window);
			json += ",\"kind\":" + json_string(item.kind);
			json += ",\"label\":" + json_string(item.label);
			json += ",\"value\":" + json_string(item.value);
			json += ",\"min\":" + json_string(item.minimum);
			json += ",\"max\":" + json_string(item.maximum);
			json += ",\"components\":" + std::to_string(item.components);
			json += ",\"changed\":" + std::string(item.changed ? "true" : "false");
			json += "}";
		}
		json += "]}";
		return json;
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
		add_widget("text", text_end != nullptr ? std::string(text, text_end) : std::string(text));
	}

	static void capture_text_v(const char *fmt, va_list args)
	{
		va_list original_args;
		va_copy(original_args, args);
		va_list capture_args;
		va_copy(capture_args, args);
		g_imgui_function_table_19250.TextV(fmt, original_args);
		va_end(original_args);
		add_widget("text", format_text_v(fmt, capture_args));
		va_end(capture_args);
	}

	static bool capture_button(const char *label, const ImVec2 &size)
	{
		const bool result = g_imgui_function_table_19250.Button(label, size);
		add_widget("button", string_or_empty(label), result ? "clicked" : "", {}, {}, 0, result);
		return result;
	}

	static bool capture_small_button(const char *label)
	{
		const bool result = g_imgui_function_table_19250.SmallButton(label);
		add_widget("button", string_or_empty(label), result ? "clicked" : "", {}, {}, 0, result);
		return result;
	}

	static bool capture_checkbox(const char *label, bool *v)
	{
		const bool result = g_imgui_function_table_19250.Checkbox(label, v);
		add_widget("checkbox", string_or_empty(label), v != nullptr ? format_bool(*v) : "", {}, {}, 1, result);
		return result;
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
		const bool result = g_imgui_function_table_19250.Combo(label, current_item, items, items_count, popup_max_height_in_items);
		std::string value = current_item != nullptr ? std::to_string(*current_item) : "";
		if (current_item != nullptr && items != nullptr && *current_item >= 0 && *current_item < items_count && items[*current_item] != nullptr)
			value += " (" + std::string(items[*current_item]) + ")";
		add_widget("combo", string_or_empty(label), value, "0", std::to_string(std::max(0, items_count - 1)), 1, result);
		return result;
	}

	static bool capture_combo2(const char *label, int *current_item, const char *items_separated_by_zeros, int popup_max_height_in_items)
	{
		const bool result = g_imgui_function_table_19250.Combo2(label, current_item, items_separated_by_zeros, popup_max_height_in_items);
		add_widget("combo", string_or_empty(label), current_item != nullptr ? std::to_string(*current_item) : "", {}, {}, 1, result);
		return result;
	}

	static bool capture_drag_float(const char *label, float *v, float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragFloat(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", format_float(v_min), format_float(v_max), 1, result);
		return result;
	}

	static bool capture_drag_float2(const char *label, float v[2], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragFloat2(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", format_float(v_min), format_float(v_max), 2, result);
		return result;
	}

	static bool capture_drag_float3(const char *label, float v[3], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragFloat3(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", format_float(v_min), format_float(v_max), 3, result);
		return result;
	}

	static bool capture_drag_float4(const char *label, float v[4], float v_speed, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragFloat4(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", format_float(v_min), format_float(v_max), 4, result);
		return result;
	}

	static bool capture_drag_int(const char *label, int *v, float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragInt(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", std::to_string(v_min), std::to_string(v_max), 1, result);
		return result;
	}

	static bool capture_drag_int2(const char *label, int v[2], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragInt2(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", std::to_string(v_min), std::to_string(v_max), 2, result);
		return result;
	}

	static bool capture_drag_int3(const char *label, int v[3], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragInt3(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", std::to_string(v_min), std::to_string(v_max), 3, result);
		return result;
	}

	static bool capture_drag_int4(const char *label, int v[4], float v_speed, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.DragInt4(label, v, v_speed, v_min, v_max, format, flags);
		add_widget("drag_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", std::to_string(v_min), std::to_string(v_max), 4, result);
		return result;
	}

	static bool capture_slider_float(const char *label, float *v, float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderFloat(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", format_float(v_min), format_float(v_max), 1, result);
		return result;
	}

	static bool capture_slider_float2(const char *label, float v[2], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderFloat2(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", format_float(v_min), format_float(v_max), 2, result);
		return result;
	}

	static bool capture_slider_float3(const char *label, float v[3], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderFloat3(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", format_float(v_min), format_float(v_max), 3, result);
		return result;
	}

	static bool capture_slider_float4(const char *label, float v[4], float v_min, float v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderFloat4(label, v, v_min, v_max, format, flags);
		add_widget("slider_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", format_float(v_min), format_float(v_max), 4, result);
		return result;
	}

	static bool capture_slider_int(const char *label, int *v, int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderInt(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", std::to_string(v_min), std::to_string(v_max), 1, result);
		return result;
	}

	static bool capture_slider_int2(const char *label, int v[2], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderInt2(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", std::to_string(v_min), std::to_string(v_max), 2, result);
		return result;
	}

	static bool capture_slider_int3(const char *label, int v[3], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderInt3(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", std::to_string(v_min), std::to_string(v_max), 3, result);
		return result;
	}

	static bool capture_slider_int4(const char *label, int v[4], int v_min, int v_max, const char *format, ImGuiSliderFlags flags)
	{
		const bool result = g_imgui_function_table_19250.SliderInt4(label, v, v_min, v_max, format, flags);
		add_widget("slider_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", std::to_string(v_min), std::to_string(v_max), 4, result);
		return result;
	}

	static bool capture_input_float(const char *label, float *v, float step, float step_fast, const char *format, ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputFloat(label, v, step, step_fast, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float(*v) : "", {}, {}, 1, result);
		return result;
	}

	static bool capture_input_float2(const char *label, float v[2], const char *format, ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputFloat2(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 2) : "", {}, {}, 2, result);
		return result;
	}

	static bool capture_input_float3(const char *label, float v[3], const char *format, ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputFloat3(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 3) : "", {}, {}, 3, result);
		return result;
	}

	static bool capture_input_float4(const char *label, float v[4], const char *format, ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputFloat4(label, v, format, flags);
		add_widget("input_float", string_or_empty(label), v != nullptr ? format_float_array(v, 4) : "", {}, {}, 4, result);
		return result;
	}

	static bool capture_input_int(const char *label, int *v, int step, int step_fast, ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputInt(label, v, step, step_fast, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? std::to_string(*v) : "", {}, {}, 1, result);
		return result;
	}

	static bool capture_input_int2(const char *label, int v[2], ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputInt2(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 2) : "", {}, {}, 2, result);
		return result;
	}

	static bool capture_input_int3(const char *label, int v[3], ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputInt3(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 3) : "", {}, {}, 3, result);
		return result;
	}

	static bool capture_input_int4(const char *label, int v[4], ImGuiInputTextFlags flags)
	{
		const bool result = g_imgui_function_table_19250.InputInt4(label, v, flags);
		add_widget("input_int", string_or_empty(label), v != nullptr ? format_int_array(v, 4) : "", {}, {}, 4, result);
		return result;
	}

	static bool capture_color_edit3(const char *label, float col[3], ImGuiColorEditFlags flags)
	{
		const bool result = g_imgui_function_table_19250.ColorEdit3(label, col, flags);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 3) : "", {}, {}, 3, result);
		return result;
	}

	static bool capture_color_edit4(const char *label, float col[4], ImGuiColorEditFlags flags)
	{
		const bool result = g_imgui_function_table_19250.ColorEdit4(label, col, flags);
		add_widget("color", string_or_empty(label), col != nullptr ? format_float_array(col, 4) : "", {}, {}, 4, result);
		return result;
	}

	static bool capture_tree_node(const char *label)
	{
		const bool result = g_imgui_function_table_19250.TreeNode(label);
		add_widget("tree_node", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, result);
		return result;
	}

	static bool capture_tree_node_ex(const char *label, ImGuiTreeNodeFlags flags)
	{
		const bool result = g_imgui_function_table_19250.TreeNodeEx(label, flags);
		add_widget("tree_node", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, result);
		return result;
	}

	static bool capture_collapsing_header(const char *label, ImGuiTreeNodeFlags flags)
	{
		const bool result = g_imgui_function_table_19250.CollapsingHeader(label, flags);
		add_widget("collapsing_header", string_or_empty(label), result ? "open" : "closed", {}, {}, 0, result);
		return result;
	}

	static bool capture_selectable(const char *label, bool selected, ImGuiSelectableFlags flags, const ImVec2 &size)
	{
		const bool result = g_imgui_function_table_19250.Selectable(label, selected, flags, size);
		add_widget("selectable", string_or_empty(label), format_bool(selected), {}, {}, 1, result);
		return result;
	}

	static bool capture_selectable2(const char *label, bool *selected, ImGuiSelectableFlags flags, const ImVec2 &size)
	{
		const bool result = g_imgui_function_table_19250.Selectable2(label, selected, flags, size);
		add_widget("selectable", string_or_empty(label), selected != nullptr ? format_bool(*selected) : "", {}, {}, 1, result);
		return result;
	}

	static bool capture_list_box(const char *label, int *current_item, const char *const items[], int items_count, int height_in_items)
	{
		const bool result = g_imgui_function_table_19250.ListBox(label, current_item, items, items_count, height_in_items);
		add_widget("list_box", string_or_empty(label), current_item != nullptr ? std::to_string(*current_item) : "", "0", std::to_string(std::max(0, items_count - 1)), 1, result);
		return result;
	}

	static bool capture_menu_item(const char *label, const char *shortcut, bool selected, bool enabled)
	{
		const bool result = g_imgui_function_table_19250.MenuItem(label, shortcut, selected, enabled);
		add_widget("menu_item", string_or_empty(label), format_bool(selected), {}, {}, 1, result);
		return result;
	}

	static bool capture_menu_item2(const char *label, const char *shortcut, bool *selected, bool enabled)
	{
		const bool result = g_imgui_function_table_19250.MenuItem2(label, shortcut, selected, enabled);
		add_widget("menu_item", string_or_empty(label), selected != nullptr ? format_bool(*selected) : "", {}, {}, 1, result);
		return result;
	}

	static const imgui_function_table_19250 &proxy_table_19250()
	{
		static const imgui_function_table_19250 table = [] {
			imgui_function_table_19250 proxy = g_imgui_function_table_19250;
			proxy.Begin = capture_begin;
			proxy.End = capture_end;
			proxy.TextUnformatted = capture_text_unformatted;
			proxy.TextV = capture_text_v;
			proxy.Button = capture_button;
			proxy.SmallButton = capture_small_button;
			proxy.Checkbox = capture_checkbox;
			proxy.RadioButton = capture_radio_button;
			proxy.RadioButton2 = capture_radio_button2;
			proxy.BeginCombo = capture_begin_combo;
			proxy.Combo = capture_combo;
			proxy.Combo2 = capture_combo2;
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
			proxy.TreeNode = capture_tree_node;
			proxy.TreeNodeEx = capture_tree_node_ex;
			proxy.CollapsingHeader = capture_collapsing_header;
			proxy.Selectable = capture_selectable;
			proxy.Selectable2 = capture_selectable2;
			proxy.ListBox = capture_list_box;
			proxy.MenuItem = capture_menu_item;
			proxy.MenuItem2 = capture_menu_item2;
			return proxy;
		}();
		return table;
	}
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

extern "C" __declspec(dllexport) const void *ReShadeGetImGuiFunctionTable(uint32_t version)
{
	reshade::imgui_capture::record_requested_version(version);

	if (version == 19250)
		return &reshade::imgui_capture::proxy_table_19250();
#if RESHADE_ADDON >= 2
	if (version == 19220 || version == 19222)
		return &g_imgui_function_table_19222;
	if (version == 19190 || version == 19191)
		return &g_imgui_function_table_19191;
	if (version == 19180)
		return &g_imgui_function_table_19180;
	if (version == 19040)
		return &g_imgui_function_table_19040;
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
