/*
 * Offline ReShade prototype
 * SPDX-License-Identifier: BSD-3-Clause
 */

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <timeapi.h>
#include <wincodec.h>
#include <wrl/client.h>

#include "reshade_api.hpp"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <iterator>
#include <deque>
#include <memory>
#include <mutex>
#include <limits>
#include <regex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>


using Microsoft::WRL::ComPtr;

namespace
{
	struct image_rgba
	{
		int width = 0;
		int height = 0;
		std::vector<uint8_t> pixels;
	};

	struct options
	{
		std::filesystem::path color_path;
		std::filesystem::path depth_path;
		std::string depth_format = "raw";
		std::filesystem::path effect_dir;
		std::filesystem::path preset_path;
		std::filesystem::path output_path;
		uint32_t width = 0;
		uint32_t height = 0;
		uintptr_t parent_hwnd = 0;
		uintptr_t overlay_hwnd = 0;
		std::string control_pipe;
		std::string preview_pipe;
		bool preview_shared = false;
		uint32_t preview_width = 0;
		uint32_t preview_height = 0;
		bool interactive = false;
		bool disable_input_watch = false;
	};

	struct reshade_exports
	{
		HMODULE module = nullptr;
		bool (*create_runtime)(reshade::api::device_api, void *, void *, void *, const char *, reshade::api::effect_runtime **) = nullptr;
		void (*destroy_runtime)(reshade::api::effect_runtime *) = nullptr;
		void (*update_and_present_runtime)(reshade::api::effect_runtime *) = nullptr;
		void (*set_external_overlay_target)(reshade::api::effect_runtime *, void *, uint32_t, uint32_t) = nullptr;
	};

	struct shared_preview_state
	{
		ComPtr<ID3D11Texture2D> texture;
		HANDLE handle = nullptr;
		uint32_t width = 0;
		uint32_t height = 0;
	};

	void print_usage()
	{
		std::cout <<
			"usage: OfflineReShadePrototype [--color <png>] [--depth <png|rfloat>] [--depth-format <raw|rgba>] [--effect-dir <dir>] [--output <png>] [--width <w> --height <h>] [--parent-hwnd <hwnd>] [--overlay-hwnd <hwnd>] [--control-pipe <name>] [--preview-pipe <name>] [--preview-shared] [--preview-width <w> --preview-height <h>] [--interactive] [--disable-input-watch]\n";
	}

	std::wstring widen_utf8(const std::string &value)
	{
		if (value.empty())
			return {};

		const int required = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
		std::wstring result(required, L'\0');
		MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), required);
		return result;
	}

	std::string narrow_utf8(const std::wstring &value)
	{
		if (value.empty())
			return {};

		const int required = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
		std::string result(required, '\0');
		WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), required, nullptr, nullptr);
		return result;
	}

	std::string path_utf8(const std::filesystem::path &path)
	{
		return narrow_utf8(path.wstring());
	}

	bool parse_uint(const wchar_t *text, uint32_t &value)
	{
		wchar_t *end = nullptr;
		const unsigned long result = std::wcstoul(text, &end, 10);
		if (end == text || *end != L'\0' || result == 0 || result > UINT32_MAX)
			return false;

		value = static_cast<uint32_t>(result);
		return true;
	}

	bool parse_hwnd_value(const wchar_t *text, uintptr_t &value)
	{
		wchar_t *end = nullptr;
		const unsigned long long result = std::wcstoull(text, &end, 0);
		if (end == text || *end != L'\0')
			return false;

		value = static_cast<uintptr_t>(result);
		return value != 0;
	}

	bool parse_options(int argc, wchar_t **argv, options &opts)
	{
		for (int i = 1; i < argc; ++i)
		{
			const std::wstring_view arg = argv[i];
			const auto require_value = [&](std::filesystem::path &out) {
				if (++i >= argc)
					return false;
				out = std::filesystem::absolute(argv[i]);
				return true;
			};

			if (arg == L"--color")
			{
				if (!require_value(opts.color_path))
					return false;
			}
			else if (arg == L"--depth")
			{
				if (!require_value(opts.depth_path))
					return false;
			}
			else if (arg == L"--depth-format")
			{
				if (++i >= argc)
					return false;
				opts.depth_format = narrow_utf8(argv[i]);
				std::transform(opts.depth_format.begin(), opts.depth_format.end(), opts.depth_format.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
				if (opts.depth_format != "raw" && opts.depth_format != "rgba")
					return false;
			}
			else if (arg == L"--effect-dir")
			{
				if (!require_value(opts.effect_dir))
					return false;
			}
			else if (arg == L"--preset")
			{
				if (!require_value(opts.preset_path))
					return false;
			}
			else if (arg == L"--output")
			{
				if (!require_value(opts.output_path))
					return false;
			}
			else if (arg == L"--width")
			{
				if (++i >= argc || !parse_uint(argv[i], opts.width))
					return false;
			}
			else if (arg == L"--height")
			{
				if (++i >= argc || !parse_uint(argv[i], opts.height))
					return false;
			}
			else if (arg == L"--parent-hwnd")
			{
				if (++i >= argc || !parse_hwnd_value(argv[i], opts.parent_hwnd))
					return false;
			}
			else if (arg == L"--overlay-hwnd")
			{
				if (++i >= argc || !parse_hwnd_value(argv[i], opts.overlay_hwnd))
					return false;
			}
			else if (arg == L"--control-pipe")
			{
				if (++i >= argc)
					return false;
				opts.control_pipe = narrow_utf8(argv[i]);
			}
			else if (arg == L"--preview-pipe")
			{
				if (++i >= argc)
					return false;
				opts.preview_pipe = narrow_utf8(argv[i]);
			}
			else if (arg == L"--preview-shared")
			{
				opts.preview_shared = true;
			}
			else if (arg == L"--preview-width")
			{
				if (++i >= argc || !parse_uint(argv[i], opts.preview_width))
					return false;
			}
			else if (arg == L"--preview-height")
			{
				if (++i >= argc || !parse_uint(argv[i], opts.preview_height))
					return false;
			}
			else if (arg == L"--interactive")
			{
				opts.interactive = true;
			}
			else if (arg == L"--disable-input-watch")
			{
				opts.disable_input_watch = true;
			}
			else if (arg == L"--help" || arg == L"-h")
			{
				print_usage();
				std::exit(0);
			}
			else
			{
				return false;
			}
		}

		return true;
	}

	FILE *open_file(const std::filesystem::path &path, const wchar_t *mode)
	{
		FILE *file = nullptr;
		_wfopen_s(&file, path.c_str(), mode);
		return file;
	}

	bool load_png_rgba(IWICImagingFactory *wic_factory, const std::filesystem::path &path, image_rgba &image)
	{
		ComPtr<IWICBitmapDecoder> decoder;
		if (FAILED(wic_factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)))
			return false;

		ComPtr<IWICBitmapFrameDecode> frame;
		if (FAILED(decoder->GetFrame(0, &frame)))
			return false;

		UINT width = 0, height = 0;
		if (FAILED(frame->GetSize(&width, &height)) || width == 0 || height == 0 || width > INT_MAX || height > INT_MAX)
			return false;

		ComPtr<IWICFormatConverter> converter;
		if (FAILED(wic_factory->CreateFormatConverter(&converter)))
			return false;
		if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
			return false;

		image.width = static_cast<int>(width);
		image.height = static_cast<int>(height);
		image.pixels.resize(static_cast<size_t>(width) * height * 4);

		return SUCCEEDED(converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(image.pixels.size()), image.pixels.data()));
	}
	image_rgba resize_rgba(const image_rgba &source, uint32_t width, uint32_t height)
	{
		if (source.width == static_cast<int>(width) && source.height == static_cast<int>(height))
			return source;

		image_rgba result;
		result.width = static_cast<int>(width);
		result.height = static_cast<int>(height);
		result.pixels.resize(static_cast<size_t>(width) * height * 4);

		for (uint32_t y = 0; y < height; ++y)
		{
			const uint32_t source_y = std::min<uint32_t>(static_cast<uint32_t>((static_cast<uint64_t>(y) * source.height) / height), source.height - 1);
			for (uint32_t x = 0; x < width; ++x)
			{
				const uint32_t source_x = std::min<uint32_t>(static_cast<uint32_t>((static_cast<uint64_t>(x) * source.width) / width), source.width - 1);
				const uint8_t *src = source.pixels.data() + (static_cast<size_t>(source_y) * source.width + source_x) * 4;
				uint8_t *dst = result.pixels.data() + (static_cast<size_t>(y) * width + x) * 4;
				std::copy_n(src, 4, dst);
			}
		}

		return result;
	}

	std::vector<float> make_default_depth_data(uint32_t width, uint32_t height)
	{
		return std::vector<float>(static_cast<size_t>(width) * height, 1.0f);
	}

	std::vector<float> make_depth_data_from_rgba(const image_rgba *depth_image, uint32_t width, uint32_t height)
	{
		std::vector<float> result(static_cast<size_t>(width) * height, 1.0f);
		if (depth_image == nullptr)
			return result;

		for (uint32_t y = 0; y < height; ++y)
		{
			const uint32_t source_y = std::min<uint32_t>(static_cast<uint32_t>((static_cast<uint64_t>(y) * depth_image->height) / height), depth_image->height - 1);
			for (uint32_t x = 0; x < width; ++x)
			{
				const uint32_t source_x = std::min<uint32_t>(static_cast<uint32_t>((static_cast<uint64_t>(x) * depth_image->width) / width), depth_image->width - 1);
				const uint8_t *src = depth_image->pixels.data() + (static_cast<size_t>(source_y) * depth_image->width + source_x) * 4;
				const uint32_t packed =
					static_cast<uint32_t>(src[0]) |
					(static_cast<uint32_t>(src[1]) << 8) |
					(static_cast<uint32_t>(src[2]) << 16) |
					(static_cast<uint32_t>(src[3]) << 24);
				result[static_cast<size_t>(y) * width + x] = static_cast<float>(packed / 4294967295.0);
			}
		}

		return result;
	}

	std::filesystem::file_time_type file_write_time_or_min(const std::filesystem::path &path)
	{
		std::error_code ec;
		const std::filesystem::file_time_type time = std::filesystem::last_write_time(path, ec);
		return ec ? std::filesystem::file_time_type::min() : time;
	}

	bool save_png_rgba(IWICImagingFactory *wic_factory, const std::filesystem::path &path, uint32_t width, uint32_t height, const std::vector<uint8_t> &pixels)
	{
		if (path.has_parent_path())
			std::filesystem::create_directories(path.parent_path());

		ComPtr<IWICStream> stream;
		if (FAILED(wic_factory->CreateStream(&stream)))
			return false;
		if (FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE)))
			return false;

		ComPtr<IWICBitmapEncoder> encoder;
		if (FAILED(wic_factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)))
			return false;
		if (FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)))
			return false;

		ComPtr<IWICBitmapFrameEncode> frame;
		ComPtr<IPropertyBag2> property_bag;
		if (FAILED(encoder->CreateNewFrame(&frame, &property_bag)))
			return false;
		if (FAILED(frame->Initialize(property_bag.Get())))
			return false;
		if (FAILED(frame->SetSize(width, height)))
			return false;

		WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
		if (FAILED(frame->SetPixelFormat(&format)) || format != GUID_WICPixelFormat32bppBGRA)
			return false;

		std::vector<uint8_t> bgra(pixels.size());
		for (size_t i = 0; i + 3 < pixels.size(); i += 4)
		{
			bgra[i + 0] = pixels[i + 2];
			bgra[i + 1] = pixels[i + 1];
			bgra[i + 2] = pixels[i + 0];
			bgra[i + 3] = pixels[i + 3];
		}

		if (FAILED(frame->WritePixels(height, width * 4, static_cast<UINT>(bgra.size()), bgra.data())))
			return false;

		return SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
	}
	std::filesystem::path executable_directory()
	{
		wchar_t path[MAX_PATH] = {};
		GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
		return std::filesystem::path(path).parent_path();
	}

	std::filesystem::path prototype_directory()
	{
		const std::filesystem::path dir = executable_directory() / L"OfflinePrototype";
		std::filesystem::create_directories(dir);
		return dir;
	}

	std::vector<std::pair<std::string, std::string>> scan_techniques(const std::filesystem::path &effect_dir)
	{
		std::vector<std::pair<std::string, std::string>> result;
		const std::regex technique_regex(R"(^\s*technique\s+([A-Za-z_][A-Za-z0-9_]*))");

		std::error_code ec;
		for (const std::filesystem::directory_entry &entry : std::filesystem::recursive_directory_iterator(effect_dir, std::filesystem::directory_options::skip_permission_denied, ec))
		{
			if (entry.is_directory(ec) || (entry.path().extension() != L".fx" && entry.path().extension() != L".addonfx"))
				continue;

			std::ifstream stream(entry.path());
			if (!stream)
				continue;

			std::string line;
			while (std::getline(stream, line))
			{
				std::smatch match;
				if (std::regex_search(line, match, technique_regex))
					result.emplace_back(match[1].str(), path_utf8(entry.path().filename()));
			}
		}

		return result;
	}

	std::filesystem::path make_preset(const options &opts, const std::filesystem::path &work_dir)
	{
		if (!opts.preset_path.empty())
			return opts.preset_path;

		const std::filesystem::path preset_path = work_dir / L"OfflinePreset.ini";
		if (std::filesystem::exists(preset_path))
			return preset_path;

		std::ofstream preset(preset_path, std::ios::binary | std::ios::trunc);
		preset << "Techniques=\n";
		preset << "TechniqueSorting=\n";
		return preset_path;
	}
	std::filesystem::path make_config(const options &opts, const std::filesystem::path &work_dir, const std::filesystem::path &preset_path)
	{
		const std::filesystem::path config_path = work_dir / L"ReShade.ini";
		const std::filesystem::path texture_dir = opts.effect_dir.parent_path() / L"Textures";

		std::ofstream config(config_path, std::ios::binary);
		config << "[GENERAL]\n";
		config << "NoDebugInfo=1\n";
		config << "NoEffectCache=1\n";
		config << "NoReloadOnInit=0\n";
		config << "PerformanceMode=0\n";
		config << "SkipLoadingDisabledEffects=0\n";
		config << "EffectSearchPaths=" << path_utf8(opts.effect_dir) << "\\**\n";
		if (std::filesystem::exists(texture_dir))
			config << "TextureSearchPaths=" << path_utf8(texture_dir) << "\\**\n";
		else
			config << "TextureSearchPaths=" << path_utf8(opts.effect_dir) << "\\**\n";
		config << "PresetPath=" << path_utf8(preset_path) << "\n";
		config << "PreprocessorDefinitions=OFFLINE_RESHADE=1\n";
		config << "\n[INPUT]\n";
		config << "KeyEffects=0,0,0,0\n";
		config << "KeyOverlay=" << (opts.interactive ? "36,0,0,0" : "0,0,0,0") << "\n";
		config << "KeyReload=0,0,0,0\n";
		config << "KeyScreenshot=" << (opts.interactive ? "44,0,0,0" : "0,0,0,0") << "\n";
		config << "\n[SCREENSHOT]\n";
		if (!opts.output_path.empty())
			config << "SavePath=" << path_utf8(opts.output_path.parent_path()) << "\n";
		else
			config << "SavePath=.\\\\\n";
		config << "FileNaming=%AppName%_%Date%_%Time%_%Count%\n";
		config << "FileFormat=1\n";
		config << "ClearAlpha=1\n";
		config << "SaveOverlayShot=0\n";
		config << "\n[OVERLAY]\n";
		config << "TutorialProgress=4\n";
		return config_path;
	}

	reshade_exports load_reshade()
	{
		reshade_exports result;
		const std::filesystem::path dll_path = executable_directory() / L"ReShade64.dll";

		SetEnvironmentVariableW(L"RESHADE_DISABLE_LOADING_CHECK", L"1");
		result.module = LoadLibraryW(dll_path.c_str());
		if (result.module == nullptr)
			return result;

		result.create_runtime = reinterpret_cast<decltype(result.create_runtime)>(GetProcAddress(result.module, "ReShadeCreateEffectRuntime"));
		result.destroy_runtime = reinterpret_cast<decltype(result.destroy_runtime)>(GetProcAddress(result.module, "ReShadeDestroyEffectRuntime"));
		result.update_and_present_runtime = reinterpret_cast<decltype(result.update_and_present_runtime)>(GetProcAddress(result.module, "ReShadeUpdateAndPresentEffectRuntime"));
		result.set_external_overlay_target = reinterpret_cast<decltype(result.set_external_overlay_target)>(GetProcAddress(result.module, "ReShadeSetExternalOverlayTarget"));
		return result;
	}

	LRESULT CALLBACK hidden_window_proc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam)
	{
		return DefWindowProcW(hwnd, msg, wparam, lparam);
	}

	HWND create_render_window(uint32_t width, uint32_t height, HWND parent_hwnd)
	{
		const wchar_t *class_name = L"OfflineReShadePrototypeWindow";

		WNDCLASSEXW wc = {};
		wc.cbSize = sizeof(wc);
		wc.lpfnWndProc = hidden_window_proc;
		wc.hInstance = GetModuleHandleW(nullptr);
		wc.lpszClassName = class_name;
		RegisterClassExW(&wc);

		const DWORD style = parent_hwnd != nullptr ? (WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN) : WS_OVERLAPPEDWINDOW;
		const DWORD ex_style = parent_hwnd != nullptr ? WS_EX_TOOLWINDOW : 0;
		return CreateWindowExW(ex_style, class_name, L"Offline ReShade Prototype", style,
			0, 0, static_cast<int>(width), static_cast<int>(height),
			nullptr, nullptr, wc.hInstance, nullptr);
	}

	bool create_device_and_swapchain(HWND hwnd, uint32_t width, uint32_t height, ComPtr<ID3D11Device> &device, ComPtr<ID3D11DeviceContext> &context, ComPtr<IDXGISwapChain> &swapchain)
	{
		DXGI_SWAP_CHAIN_DESC swapchain_desc = {};
		swapchain_desc.BufferDesc.Width = width;
		swapchain_desc.BufferDesc.Height = height;
		swapchain_desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		swapchain_desc.BufferDesc.RefreshRate.Numerator = 60;
		swapchain_desc.BufferDesc.RefreshRate.Denominator = 1;
		swapchain_desc.SampleDesc.Count = 1;
		swapchain_desc.SampleDesc.Quality = 0;
		swapchain_desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT;
		swapchain_desc.BufferCount = 1;
		swapchain_desc.OutputWindow = hwnd;
		swapchain_desc.Windowed = TRUE;
		swapchain_desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

		D3D_FEATURE_LEVEL feature_levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
		D3D_FEATURE_LEVEL created_level = D3D_FEATURE_LEVEL_11_0;
		UINT flags = 0;
#if defined(_DEBUG)
		flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

		HRESULT hr = D3D11CreateDeviceAndSwapChain(
			nullptr,
			D3D_DRIVER_TYPE_HARDWARE,
			nullptr,
			flags,
			feature_levels,
			static_cast<UINT>(std::size(feature_levels)),
			D3D11_SDK_VERSION,
			&swapchain_desc,
			&swapchain,
			&device,
			&created_level,
			&context);

#if defined(_DEBUG)
		if (hr == DXGI_ERROR_SDK_COMPONENT_MISSING)
		{
			flags &= ~D3D11_CREATE_DEVICE_DEBUG;
			hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, feature_levels, static_cast<UINT>(std::size(feature_levels)), D3D11_SDK_VERSION, &swapchain_desc, &swapchain, &device, &created_level, &context);
		}
#endif

		return SUCCEEDED(hr);
	}

	bool client_size(HWND hwnd, uint32_t &width, uint32_t &height)
	{
		RECT rect = {};
		if (!GetClientRect(hwnd, &rect))
			return false;

		width = static_cast<uint32_t>(std::max<LONG>(1, rect.right - rect.left));
		height = static_cast<uint32_t>(std::max<LONG>(1, rect.bottom - rect.top));
		return true;
	}

	bool create_overlay_swapchain(ID3D11Device *device, HWND hwnd, uint32_t width, uint32_t height, ComPtr<IDXGISwapChain> &swapchain)
	{
		ComPtr<IDXGIDevice> dxgi_device;
		ComPtr<IDXGIAdapter> adapter;
		ComPtr<IDXGIFactory> factory;
		if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi_device))) ||
			FAILED(dxgi_device->GetAdapter(&adapter)) ||
			FAILED(adapter->GetParent(IID_PPV_ARGS(&factory))))
			return false;

		DXGI_SWAP_CHAIN_DESC desc = {};
		desc.BufferDesc.Width = width;
		desc.BufferDesc.Height = height;
		desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		desc.BufferDesc.RefreshRate.Numerator = 60;
		desc.BufferDesc.RefreshRate.Denominator = 1;
		desc.SampleDesc.Count = 1;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.BufferCount = 1;
		desc.OutputWindow = hwnd;
		desc.Windowed = TRUE;
		desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

		return SUCCEEDED(factory->CreateSwapChain(device, &desc, &swapchain));
	}

	bool create_backbuffer_rtv(ID3D11Device *device, IDXGISwapChain *swapchain, ComPtr<ID3D11RenderTargetView> &rtv)
	{
		ComPtr<ID3D11Texture2D> backbuffer;
		if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
			return false;
		return SUCCEEDED(device->CreateRenderTargetView(backbuffer.Get(), nullptr, &rtv));
	}

	bool ensure_overlay_target(ID3D11Device *device, ID3D11DeviceContext *context, HWND hwnd, ComPtr<IDXGISwapChain> &swapchain, ComPtr<ID3D11RenderTargetView> &rtv, uint32_t &width, uint32_t &height)
	{
		if (hwnd == nullptr || !IsWindow(hwnd))
			return false;

		uint32_t new_width = 0, new_height = 0;
		if (!client_size(hwnd, new_width, new_height))
			return false;

		if (swapchain == nullptr)
		{
			width = new_width;
			height = new_height;
			if (!create_overlay_swapchain(device, hwnd, width, height, swapchain))
				return false;
			return create_backbuffer_rtv(device, swapchain.Get(), rtv);
		}

		if (new_width != width || new_height != height || rtv == nullptr)
		{
			rtv.Reset();
			context->OMSetRenderTargets(0, nullptr, nullptr);
			if (FAILED(swapchain->ResizeBuffers(0, new_width, new_height, DXGI_FORMAT_UNKNOWN, 0)))
				return false;
			width = new_width;
			height = new_height;
			return create_backbuffer_rtv(device, swapchain.Get(), rtv);
		}

		return true;
	}
	bool create_shader_resource(ID3D11Device *device, DXGI_FORMAT format, uint32_t width, uint32_t height, const void *data, uint32_t row_pitch, ComPtr<ID3D11Texture2D> &texture, ComPtr<ID3D11ShaderResourceView> &srv)
	{
		D3D11_TEXTURE2D_DESC desc = {};
		desc.Width = width;
		desc.Height = height;
		desc.MipLevels = 1;
		desc.ArraySize = 1;
		desc.Format = format;
		desc.SampleDesc.Count = 1;
		desc.Usage = D3D11_USAGE_DEFAULT;
		desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

		D3D11_SUBRESOURCE_DATA initial_data = {};
		initial_data.pSysMem = data;
		initial_data.SysMemPitch = row_pitch;

		if (FAILED(device->CreateTexture2D(&desc, &initial_data, &texture)))
			return false;
		if (FAILED(device->CreateShaderResourceView(texture.Get(), nullptr, &srv)))
			return false;

		return true;
	}

	bool create_shared_preview_texture(ID3D11Device *device, uint32_t width, uint32_t height, shared_preview_state &preview)
	{
		D3D11_TEXTURE2D_DESC desc = {};
		desc.Width = width;
		desc.Height = height;
		desc.MipLevels = 1;
		desc.ArraySize = 1;
		desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		desc.SampleDesc.Count = 1;
		desc.Usage = D3D11_USAGE_DEFAULT;
		desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

		ComPtr<ID3D11Texture2D> texture;
		if (FAILED(device->CreateTexture2D(&desc, nullptr, &texture)))
			return false;

		ComPtr<IDXGIResource> resource;
		HANDLE handle = nullptr;
		if (FAILED(texture.As(&resource)) || FAILED(resource->GetSharedHandle(&handle)) || handle == nullptr)
			return false;

		preview.texture = std::move(texture);
		preview.handle = handle;
		preview.width = width;
		preview.height = height;
		return true;
	}

	bool copy_backbuffer_to_texture(ID3D11DeviceContext *context, IDXGISwapChain *swapchain, ID3D11Texture2D *target)
	{
		ComPtr<ID3D11Texture2D> backbuffer;
		if (target == nullptr || FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
			return false;

		context->CopyResource(target, backbuffer.Get());
		return true;
	}

	bool copy_rgba_to_backbuffer(ID3D11DeviceContext *context, IDXGISwapChain *swapchain, uint32_t width, uint32_t height, const std::vector<uint8_t> &pixels)
	{
		ComPtr<ID3D11Texture2D> backbuffer;
		if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
			return false;

		context->UpdateSubresource(backbuffer.Get(), 0, nullptr, pixels.data(), width * 4, width * height * 4);
		return true;
	}

	std::vector<uint8_t> read_backbuffer(ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain *swapchain, uint32_t width, uint32_t height)
	{
		ComPtr<ID3D11Texture2D> backbuffer;
		if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
			return {};

		D3D11_TEXTURE2D_DESC desc = {};
		backbuffer->GetDesc(&desc);
		desc.Usage = D3D11_USAGE_STAGING;
		desc.BindFlags = 0;
		desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
		desc.MiscFlags = 0;

		ComPtr<ID3D11Texture2D> staging;
		if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging)))
			return {};

		context->CopyResource(staging.Get(), backbuffer.Get());
		context->Flush();

		D3D11_MAPPED_SUBRESOURCE mapped = {};
		if (FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
			return {};

		std::vector<uint8_t> result(static_cast<size_t>(width) * height * 4);
		for (uint32_t y = 0; y < height; ++y)
			std::copy_n(static_cast<const uint8_t *>(mapped.pData) + static_cast<size_t>(mapped.RowPitch) * y, width * 4, result.data() + static_cast<size_t>(width) * y * 4);

		context->Unmap(staging.Get(), 0);
		return result;
	}

	class async_backbuffer_reader
	{
	public:
		bool initialize(ID3D11Device *device, IDXGISwapChain *swapchain, uint32_t width, uint32_t height, uint32_t slot_count = 4)
		{
			_width = width;
			_height = height;
			_slots.clear();
			_next_copy_slot = 0;

			ComPtr<ID3D11Texture2D> backbuffer;
			if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
				return false;

			D3D11_TEXTURE2D_DESC desc = {};
			backbuffer->GetDesc(&desc);
			desc.Usage = D3D11_USAGE_STAGING;
			desc.BindFlags = 0;
			desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
			desc.MiscFlags = 0;

			_slots.resize(std::max(2u, slot_count));
			for (slot &entry : _slots)
			{
				if (FAILED(device->CreateTexture2D(&desc, nullptr, &entry.texture)))
				{
					_slots.clear();
					return false;
				}
			}

			return true;
		}

		std::vector<uint8_t> submit_and_try_read(ID3D11DeviceContext *context, IDXGISwapChain *swapchain)
		{
			std::vector<uint8_t> result = try_read_ready_frame(context);
			submit_copy(context, swapchain);
			return result;
		}

	private:
		struct slot
		{
			ComPtr<ID3D11Texture2D> texture;
			bool pending = false;
		};

		std::vector<uint8_t> try_read_ready_frame(ID3D11DeviceContext *context)
		{
			for (slot &entry : _slots)
			{
				if (!entry.pending)
					continue;

				D3D11_MAPPED_SUBRESOURCE mapped = {};
				const HRESULT hr = context->Map(entry.texture.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
				if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
					continue;
				if (FAILED(hr))
				{
					entry.pending = false;
					continue;
				}

				std::vector<uint8_t> result(static_cast<size_t>(_width) * _height * 4);
				for (uint32_t y = 0; y < _height; ++y)
					std::copy_n(static_cast<const uint8_t *>(mapped.pData) + static_cast<size_t>(mapped.RowPitch) * y, _width * 4, result.data() + static_cast<size_t>(_width) * y * 4);

				context->Unmap(entry.texture.Get(), 0);
				entry.pending = false;
				return result;
			}

			return {};
		}

		void submit_copy(ID3D11DeviceContext *context, IDXGISwapChain *swapchain)
		{
			if (_slots.empty())
				return;

			for (size_t attempt = 0; attempt < _slots.size(); ++attempt)
			{
				const size_t index = (_next_copy_slot + attempt) % _slots.size();
				slot &entry = _slots[index];
				if (entry.pending)
					continue;

				ComPtr<ID3D11Texture2D> backbuffer;
				if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
					return;

				context->CopyResource(entry.texture.Get(), backbuffer.Get());
				entry.pending = true;
				_next_copy_slot = (index + 1) % _slots.size();
				return;
			}
		}

		uint32_t _width = 0;
		uint32_t _height = 0;
		size_t _next_copy_slot = 0;
		std::vector<slot> _slots;
	};

	std::vector<uint8_t> scale_rgba_to_bgra_nearest(const std::vector<uint8_t> &source, uint32_t source_width, uint32_t source_height, uint32_t target_width, uint32_t target_height)
	{
		std::vector<uint8_t> result(static_cast<size_t>(target_width) * target_height * 4);
		if (source.empty() || source_width == 0 || source_height == 0 || target_width == 0 || target_height == 0)
			return result;

		for (uint32_t y = 0; y < target_height; ++y)
		{
			const uint32_t source_y = std::min(source_height - 1, static_cast<uint32_t>((static_cast<uint64_t>(y) * source_height) / target_height));
			for (uint32_t x = 0; x < target_width; ++x)
			{
				const uint32_t source_x = std::min(source_width - 1, static_cast<uint32_t>((static_cast<uint64_t>(x) * source_width) / target_width));
				const size_t source_index = (static_cast<size_t>(source_y) * source_width + source_x) * 4;
				const size_t target_index = (static_cast<size_t>(y) * target_width + x) * 4;
				result[target_index + 0] = source[source_index + 2];
				result[target_index + 1] = source[source_index + 1];
				result[target_index + 2] = source[source_index + 0];
				result[target_index + 3] = source[source_index + 3];
			}
		}

		return result;
	}

	bool load_rfloat_depth(const std::filesystem::path &path, uint32_t width, uint32_t height, std::vector<float> &result)
	{
		const uint64_t expected_size = static_cast<uint64_t>(width) * height * sizeof(float);
		std::error_code ec;
		const uint64_t actual_size = std::filesystem::file_size(path, ec);
		if (ec || actual_size != expected_size || expected_size > static_cast<uint64_t>(std::numeric_limits<size_t>::max()))
			return false;

		std::ifstream stream(path, std::ios::binary);
		if (!stream)
			return false;

		std::vector<float> bottom_to_top(static_cast<size_t>(width) * height);
		stream.read(reinterpret_cast<char *>(bottom_to_top.data()), static_cast<std::streamsize>(expected_size));
		if (stream.gcount() != static_cast<std::streamsize>(expected_size))
			return false;

		result.resize(static_cast<size_t>(width) * height);
		for (uint32_t y = 0; y < height; ++y)
		{
			const float *src = bottom_to_top.data() + static_cast<size_t>(height - 1 - y) * width;
			float *dst = result.data() + static_cast<size_t>(y) * width;
			std::copy_n(src, width, dst);
		}
		return true;
	}

	bool load_depth_data(IWICImagingFactory *wic_factory, const options &opts, uint32_t width, uint32_t height, std::vector<float> &result)
	{
		if (opts.depth_path.empty())
		{
			result = make_default_depth_data(width, height);
			return true;
		}

		if (opts.depth_format == "raw")
			return load_rfloat_depth(opts.depth_path, width, height, result);

		image_rgba depth_image;
		if (!load_png_rgba(wic_factory, opts.depth_path, depth_image))
			return false;
		result = make_depth_data_from_rgba(&depth_image, width, height);
		return true;
	}

	std::vector<uint8_t> convert_rgba_to_bgra(const std::vector<uint8_t> &source)
	{
		std::vector<uint8_t> result(source.size());
		for (size_t i = 0; i + 3 < source.size(); i += 4)
		{
			result[i + 0] = source[i + 2];
			result[i + 1] = source[i + 1];
			result[i + 2] = source[i + 0];
			result[i + 3] = source[i + 3];
		}
		return result;
	}

	std::vector<uint8_t> make_preview_frame_bgra(const std::vector<uint8_t> &source, uint32_t source_width, uint32_t source_height, uint32_t target_width, uint32_t target_height)
	{
		if (source_width == target_width && source_height == target_height)
			return convert_rgba_to_bgra(source);
		return scale_rgba_to_bgra_nearest(source, source_width, source_height, target_width, target_height);
	}

	struct frame_rate_stats
	{
		void mark_render_frame()
		{
			mark_frame(_render_count, _render_start, _render_fps);
		}

		void mark_preview_frame()
		{
			mark_frame(_preview_count, _preview_start, _preview_fps);
		}

		double render_fps() const
		{
			return _render_fps.load(std::memory_order_relaxed);
		}

		double preview_fps() const
		{
			return _preview_fps.load(std::memory_order_relaxed);
		}

	private:
		static void mark_frame(uint32_t &count, std::chrono::steady_clock::time_point &start, std::atomic<double> &fps)
		{
			if (count++ == 0)
			{
				start = std::chrono::steady_clock::now();
				return;
			}

			const auto now = std::chrono::steady_clock::now();
			const auto elapsed = std::chrono::duration<double>(now - start).count();
			if (elapsed >= 1.0)
			{
				fps.store(static_cast<double>(count) / elapsed, std::memory_order_relaxed);
				count = 0;
				start = now;
			}
		}

		uint32_t _render_count = 0;
		uint32_t _preview_count = 0;
		std::chrono::steady_clock::time_point _render_start = std::chrono::steady_clock::now();
		std::chrono::steady_clock::time_point _preview_start = std::chrono::steady_clock::now();
		std::atomic<double> _render_fps = 0.0;
		std::atomic<double> _preview_fps = 0.0;
	};

	class timer_resolution_scope
	{
	public:
		timer_resolution_scope()
		{
			_enabled = timeBeginPeriod(1) == TIMERR_NOERROR;
		}

		~timer_resolution_scope()
		{
			if (_enabled)
				timeEndPeriod(1);
		}

	private:
		bool _enabled = false;
	};

	class preview_stream_server
	{
	public:
		explicit preview_stream_server(std::string pipe_name) : _pipe_name(std::move(pipe_name)) {}

		~preview_stream_server()
		{
			stop();
		}

		void start()
		{
			_running = true;
			_thread = std::thread([this]() { pipe_thread(); });
			std::cout << "PREVIEW_PIPE=" << _pipe_name << std::endl;
		}

		void stop()
		{
			if (!_running.exchange(false))
				return;
			_cv.notify_all();
			if (_pipe != INVALID_HANDLE_VALUE)
			{
				CancelIoEx(_pipe, nullptr);
				DisconnectNamedPipe(_pipe);
				CloseHandle(_pipe);
				_pipe = INVALID_HANDLE_VALUE;
			}
			if (_thread.joinable())
				_thread.join();
		}

		void publish(uint32_t width, uint32_t height, std::vector<uint8_t> pixels)
		{
			if (!_running)
				return;

			std::lock_guard<std::mutex> lock(_mutex);
			_width = width;
			_height = height;
			_pixels = std::move(pixels);
			_has_frame = true;
			++_frame_id;
			_cv.notify_one();
		}

	private:
		void pipe_thread()
		{
			const std::wstring pipe_path = L"\\\\.\\pipe\\" + widen_utf8(_pipe_name);
			while (_running)
			{
				_pipe = CreateNamedPipeW(pipe_path.c_str(), PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 16 << 20, 16 << 20, 0, nullptr);
				if (_pipe == INVALID_HANDLE_VALUE)
					return;

				const BOOL connected = ConnectNamedPipe(_pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
				if (connected)
					serve_client(_pipe);

				DisconnectNamedPipe(_pipe);
				CloseHandle(_pipe);
				_pipe = INVALID_HANDLE_VALUE;
			}
		}

		void serve_client(HANDLE pipe)
		{
			uint64_t last_frame = 0;
			while (_running)
			{
				uint32_t width = 0;
				uint32_t height = 0;
				std::vector<uint8_t> pixels;
				{
					std::unique_lock<std::mutex> lock(_mutex);
					_cv.wait(lock, [&]() { return !_running || (_has_frame && _frame_id != last_frame); });
					if (!_running)
						break;
					last_frame = _frame_id;
					width = _width;
					height = _height;
					pixels = _pixels;
				}

				const uint32_t magic = 0x4650524f; // ORPF
				const uint32_t byte_count = static_cast<uint32_t>(pixels.size());
				const uint32_t header[4] = { magic, width, height, byte_count };
				DWORD written = 0;
				if (!WriteFile(pipe, header, sizeof(header), &written, nullptr) || written != sizeof(header))
					break;
				if (byte_count != 0 && (!WriteFile(pipe, pixels.data(), byte_count, &written, nullptr) || written != byte_count))
					break;
			}
		}

		std::string _pipe_name;
		std::atomic_bool _running = false;
		std::thread _thread;
		std::mutex _mutex;
		std::condition_variable _cv;
		HANDLE _pipe = INVALID_HANDLE_VALUE;
		std::vector<uint8_t> _pixels;
		uint32_t _width = 0;
		uint32_t _height = 0;
		uint64_t _frame_id = 0;
		bool _has_frame = false;
	};

	std::string json_escape(const std::string &value)
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
				if (static_cast<unsigned char>(c) < 0x20)
					result += ' ';
				else
					result += c;
				break;
			}
		}
		return result;
	}

	std::string json_string(const std::string &value)
	{
		return "\"" + json_escape(value) + "\"";
	}

	std::string json_string_array_from_nul_list(const std::string &value)
	{
		std::string result = "[";
		bool first = true;
		size_t start = 0;
		while (start < value.size())
		{
			size_t end = value.find('\0', start);
			if (end == std::string::npos)
				end = value.size();
			std::string item = value.substr(start, end - start);
			if (!item.empty() && item.front() == '\\')
				item.erase(item.begin());
			if (!item.empty())
			{
				if (!first)
					result += ',';
				first = false;
				result += json_string(item);
			}
			start = end + 1;
		}
		result += "]";
		return result;
	}

	bool uniform_annotation_number(reshade::api::effect_runtime *runtime, reshade::api::effect_uniform_variable variable, const char *name, double &value)
	{
		float float_value = 0.0f;
		if (runtime->get_annotation_float_from_uniform_variable(variable, name, &float_value, 1))
		{
			value = float_value;
			return true;
		}
		int32_t int_value = 0;
		if (runtime->get_annotation_int_from_uniform_variable(variable, name, &int_value, 1))
		{
			value = int_value;
			return true;
		}
		uint32_t uint_value = 0;
		if (runtime->get_annotation_uint_from_uniform_variable(variable, name, &uint_value, 1))
		{
			value = uint_value;
			return true;
		}
		return false;
	}
	bool json_get_raw_string(const std::string &json, const std::string &key, std::string &value)
	{
		const std::regex pattern("\\\"" + key + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
		std::smatch match;
		if (!std::regex_search(json, match, pattern))
			return false;

		value.clear();
		const std::string raw = match[1].str();
		for (size_t i = 0; i < raw.size(); ++i)
		{
			if (raw[i] == '\\' && i + 1 < raw.size())
			{
				const char escaped = raw[++i];
				switch (escaped)
				{
				case 'n': value += '\n'; break;
				case 'r': value += '\r'; break;
				case 't': value += '\t'; break;
				default: value += escaped; break;
				}
			}
			else
			{
				value += raw[i];
			}
		}
		return true;
	}

	bool json_get_int(const std::string &json, const std::string &key, int &value)
	{
		const std::regex pattern("\\\"" + key + "\\\"\\s*:\\s*(-?\\d+)");
		std::smatch match;
		if (!std::regex_search(json, match, pattern))
			return false;
		value = std::stoi(match[1].str());
		return true;
	}

	bool json_get_bool(const std::string &json, const std::string &key, bool &value)
	{
		const std::regex pattern("\\\"" + key + "\\\"\\s*:\\s*(true|false)");
		std::smatch match;
		if (!std::regex_search(json, match, pattern))
			return false;
		value = match[1].str() == "true";
		return true;
	}

	std::vector<std::string> json_get_string_array(const std::string &json, const std::string &key)
	{
		std::vector<std::string> values;
		const std::regex array_pattern("\\\"" + key + "\\\"\\s*:\\s*\\[([^\\]]*)\\]");
		std::smatch match;
		if (!std::regex_search(json, match, array_pattern))
			return values;

		const std::string body = match[1].str();
		const std::regex string_pattern("\\\"((?:\\\\.|[^\\\"])*)\\\"");
		for (auto it = std::sregex_iterator(body.begin(), body.end(), string_pattern); it != std::sregex_iterator(); ++it)
		{
			std::string value;
			json_get_raw_string("{\"v\":" + it->str() + "}", "v", value);
			values.push_back(value);
		}
		return values;
	}
	std::vector<double> json_get_number_values(const std::string &json, const std::string &key)
	{
		std::vector<double> values;
		const std::regex array_pattern("\\\"" + key + "\\\"\\s*:\\s*\\[([^\\]]*)\\]");
		std::smatch match;
		if (std::regex_search(json, match, array_pattern))
		{
			const std::string body = match[1].str();
			const std::regex number_pattern("-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?");
			for (auto it = std::sregex_iterator(body.begin(), body.end(), number_pattern); it != std::sregex_iterator(); ++it)
				values.push_back(std::stod(it->str()));
			return values;
		}

		const std::regex number_pattern("\\\"" + key + "\\\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)");
		if (std::regex_search(json, match, number_pattern))
			values.push_back(std::stod(match[1].str()));
		return values;
	}

	std::string runtime_string(const std::function<void(char *, size_t *)> &getter)
	{
		size_t size = 0;
		getter(nullptr, &size);
		if (size == 0)
			return {};
		std::string value(size, '\0');
		getter(value.data(), &size);
		while (!value.empty() && value.back() == '\0')
			value.pop_back();
		return value;
	}

	bool split_effect_id(const std::string &id, std::string &effect_name, std::string &name)
	{
		const size_t separator = id.find("::");
		if (separator == std::string::npos)
			return false;
		effect_name = id.substr(0, separator);
		name = id.substr(separator + 2);
		return !effect_name.empty() && !name.empty();
	}

	std::string format_name(reshade::api::format format)
	{
		switch (format)
		{
		case reshade::api::format::r32_float: return "float";
		case reshade::api::format::r32_sint: return "int";
		case reshade::api::format::r32_uint: return "uint";
		case reshade::api::format::r32_typeless: return "bool";
		default: return "unknown";
		}
	}

	std::string uniform_annotation_string(reshade::api::effect_runtime *runtime, reshade::api::effect_uniform_variable variable, const char *name)
	{
		size_t size = 0;
		if (!runtime->get_annotation_string_from_uniform_variable(variable, name, nullptr, &size) || size == 0)
			return {};
		std::string value(size, '\0');
		if (!runtime->get_annotation_string_from_uniform_variable(variable, name, value.data(), &size))
			return {};
		while (!value.empty() && value.back() == '\0')
			value.pop_back();
		return value;
	}

	bool uniform_annotation_bool(reshade::api::effect_runtime *runtime, reshade::api::effect_uniform_variable variable, const char *name)
	{
		bool value = false;
		return runtime->get_annotation_bool_from_uniform_variable(variable, name, &value, 1) && value;
	}

	bool uniform_annotation_float(reshade::api::effect_runtime *runtime, reshade::api::effect_uniform_variable variable, const char *name, float &value)
	{
		return runtime->get_annotation_float_from_uniform_variable(variable, name, &value, 1);
	}

	struct technique_list_state
	{
		std::string result;
		bool first = true;
	};

	std::string build_techniques_json(reshade::api::effect_runtime *runtime)
	{
		technique_list_state state;
		state.result = "[";
		runtime->enumerate_techniques(nullptr, [](reshade::api::effect_runtime *runtime, reshade::api::effect_technique technique, void *user_data) {
			auto &state = *static_cast<technique_list_state *>(user_data);
			const std::string name = runtime_string([&](char *buffer, size_t *size) { runtime->get_technique_name(technique, buffer, size); });
			const std::string effect_name = runtime_string([&](char *buffer, size_t *size) { runtime->get_technique_effect_name(technique, buffer, size); });
			if (!state.first)
				state.result += ',';
			state.first = false;
			state.result += "{\"id\":" + json_string(effect_name + "::" + name) + ",\"effectName\":" + json_string(effect_name) + ",\"name\":" + json_string(name) + ",\"enabled\":" + (runtime->get_technique_state(technique) ? "true" : "false") + "}";
		}, &state);
		state.result += "]";
		return state.result;
	}
	struct uniform_list_state
	{
		std::string result;
		bool first = true;
	};

	std::string build_uniforms_json(reshade::api::effect_runtime *runtime)
	{
		uniform_list_state state;
		state.result = "[";
		runtime->enumerate_uniform_variables(nullptr, [](reshade::api::effect_runtime *runtime, reshade::api::effect_uniform_variable variable, void *user_data) {
			auto &state = *static_cast<uniform_list_state *>(user_data);
			if (uniform_annotation_bool(runtime, variable, "hidden"))
				return;
			if (!uniform_annotation_string(runtime, variable, "source").empty())
				return;

			reshade::api::format base_type = reshade::api::format::unknown;
			uint32_t rows = 0, columns = 0, array_length = 0;
			runtime->get_uniform_variable_type(variable, &base_type, &rows, &columns, &array_length);
			const uint32_t components = std::max(1u, rows) * std::max(1u, columns);
			const std::string name = runtime_string([&](char *buffer, size_t *size) { runtime->get_uniform_variable_name(variable, buffer, size); });
			const std::string effect_name = runtime_string([&](char *buffer, size_t *size) { runtime->get_uniform_variable_effect_name(variable, buffer, size); });
			if (name.empty() || effect_name.empty())
				return;

			if (!state.first)
				state.result += ',';
			state.first = false;
			state.result += "{\"id\":" + json_string(effect_name + "::" + name) + ",\"effectName\":" + json_string(effect_name) + ",\"name\":" + json_string(name) + ",\"type\":" + json_string(format_name(base_type)) + ",\"rows\":" + std::to_string(rows) + ",\"columns\":" + std::to_string(columns) + ",\"arrayLength\":" + std::to_string(array_length);

			const std::string label = uniform_annotation_string(runtime, variable, "ui_label");
			const std::string category = uniform_annotation_string(runtime, variable, "ui_category");
			const std::string ui_type = uniform_annotation_string(runtime, variable, "ui_type");
			const std::string ui_items = uniform_annotation_string(runtime, variable, "ui_items");
			if (!label.empty()) state.result += ",\"label\":" + json_string(label);
			if (!category.empty()) state.result += ",\"category\":" + json_string(category);
			if (!ui_type.empty()) state.result += ",\"uiType\":" + json_string(ui_type);
			if (!ui_items.empty()) state.result += ",\"items\":" + json_string_array_from_nul_list(ui_items);
			double min_value = 0.0, max_value = 0.0, step_value = 0.0;
			if (uniform_annotation_number(runtime, variable, "ui_min", min_value) || uniform_annotation_number(runtime, variable, "ui_minimum", min_value)) state.result += ",\"min\":" + std::to_string(min_value);
			if (uniform_annotation_number(runtime, variable, "ui_max", max_value) || uniform_annotation_number(runtime, variable, "ui_maximum", max_value)) state.result += ",\"max\":" + std::to_string(max_value);
			if (uniform_annotation_number(runtime, variable, "ui_step", step_value)) state.result += ",\"step\":" + std::to_string(step_value);

			state.result += ",\"value\":[";
			for (uint32_t i = 0; i < components; ++i)
			{
				if (i != 0)
					state.result += ',';
				if (base_type == reshade::api::format::r32_float)
				{
					float values[16] = {};
					runtime->get_uniform_value_float(variable, values, components);
					state.result += std::to_string(values[i]);
				}
				else if (base_type == reshade::api::format::r32_sint)
				{
					int32_t values[16] = {};
					runtime->get_uniform_value_int(variable, values, components);
					state.result += std::to_string(values[i]);
				}
				else if (base_type == reshade::api::format::r32_uint)
				{
					uint32_t values[16] = {};
					runtime->get_uniform_value_uint(variable, values, components);
					state.result += std::to_string(values[i]);
				}
				else
				{
					bool values[16] = {};
					runtime->get_uniform_value_bool(variable, values, components);
					state.result += values[i] ? "true" : "false";
				}
			}
			state.result += "]}";
		}, &state);
		state.result += "]";
		return state.result;
	}

	struct preprocessor_list_state
	{
		std::string result;
		bool first = true;
	};

	std::string build_preprocessor_definitions_json(reshade::api::effect_runtime *runtime)
	{
		preprocessor_list_state state;
		state.result = "[";
		runtime->enumerate_preprocessor_definitions(nullptr, [](reshade::api::effect_runtime *, const char *effect_name, const char *name, const char *default_value, const char *current_value, void *user_data) {
			auto &state = *static_cast<preprocessor_list_state *>(user_data);
			if (!state.first)
				state.result += ',';
			state.first = false;
			const std::string effect = effect_name != nullptr ? effect_name : "";
			const std::string definition_name = name != nullptr ? name : "";
			state.result += "{\"id\":" + json_string(effect + "::" + definition_name) + ",\"effectName\":" + json_string(effect) + ",\"name\":" + json_string(definition_name) + ",\"defaultValue\":" + json_string(default_value != nullptr ? default_value : "") + ",\"value\":" + json_string(current_value != nullptr ? current_value : "") + "}";
		}, &state);
		state.result += "]";
		return state.result;
	}
	std::string make_response(int id, const std::string &result)
	{
		return "{\"id\":" + std::to_string(id) + ",\"ok\":true,\"result\":" + result + "}";
	}

	std::string make_error(int id, const std::string &code, const std::string &message)
	{
		return "{\"id\":" + std::to_string(id) + ",\"ok\":false,\"error\":{\"code\":" + json_string(code) + ",\"message\":" + json_string(message) + "}}";
	}

	struct control_command
	{
		int id = 0;
		std::string method;
		std::string params;
		std::string response;
		bool done = false;
		std::mutex mutex;
		std::condition_variable cv;
	};

	class control_server
	{
	public:
		using input_switch_callback = std::function<std::string(const std::filesystem::path &, const std::filesystem::path &, const std::string &, const std::filesystem::path &)>;
		using save_output_callback = std::function<std::string()>;
		using input_watch_callback = std::function<void(bool)>;

		control_server(std::string pipe_name, reshade::api::effect_runtime *runtime, uint32_t width, uint32_t height, std::filesystem::path output_path, const frame_rate_stats *stats, const shared_preview_state *shared_preview, input_switch_callback input_switch, save_output_callback save_output, input_watch_callback input_watch) :
			_pipe_name(std::move(pipe_name)), _runtime(runtime), _width(width), _height(height), _output_path(std::move(output_path)), _stats(stats), _shared_preview(shared_preview), _input_switch(std::move(input_switch)), _save_output(std::move(save_output)), _input_watch(std::move(input_watch))
		{
		}

		~control_server()
		{
			stop();
		}

		void start()
		{
			_running = true;
			_thread = std::thread([this]() { pipe_thread(); });
			std::cout << "CONTROL_PIPE=" << _pipe_name << std::endl;
			std::cout << "CONTROL_READY=1" << std::endl;
		}

		void stop()
		{
			if (!_running.exchange(false))
				return;
			if (_pipe != INVALID_HANDLE_VALUE)
			{
				CancelIoEx(_pipe, nullptr);
				DisconnectNamedPipe(_pipe);
				CloseHandle(_pipe);
				_pipe = INVALID_HANDLE_VALUE;
			}
			if (_thread.joinable())
				_thread.join();
		}

		void process_pending()
		{
			for (;;)
			{
				std::shared_ptr<control_command> command;
				{
					std::lock_guard<std::mutex> lock(_queue_mutex);
					if (_queue.empty())
						break;
					command = _queue.front();
					_queue.pop_front();
				}

				std::string response = execute(*command);
				{
					std::lock_guard<std::mutex> lock(command->mutex);
					command->response = std::move(response);
					command->done = true;
				}
				command->cv.notify_one();
			}
		}

	private:
		void pipe_thread()
		{
			const std::wstring pipe_path = L"\\\\.\\pipe\\" + widen_utf8(_pipe_name);
			while (_running)
			{
				_pipe = CreateNamedPipeW(pipe_path.c_str(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 1 << 20, 1 << 20, 0, nullptr);
				if (_pipe == INVALID_HANDLE_VALUE)
					return;

				const BOOL connected = ConnectNamedPipe(_pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
				if (connected)
					serve_client(_pipe);

				DisconnectNamedPipe(_pipe);
				CloseHandle(_pipe);
				_pipe = INVALID_HANDLE_VALUE;
			}
		}

		void serve_client(HANDLE pipe)
		{
			std::string pending;
			char buffer[4096];
			while (_running)
			{
				DWORD bytes_read = 0;
				if (!ReadFile(pipe, buffer, sizeof(buffer), &bytes_read, nullptr) || bytes_read == 0)
					break;
				pending.append(buffer, buffer + bytes_read);
				for (;;)
				{
					const size_t newline = pending.find('\n');
					if (newline == std::string::npos)
						break;
					std::string line = pending.substr(0, newline);
					pending.erase(0, newline + 1);
					if (!line.empty() && line.back() == '\r')
						line.pop_back();
					const std::string response = handle_line(line);
					DWORD bytes_written = 0;
					const std::string output = response + "\n";
					if (!WriteFile(pipe, output.data(), static_cast<DWORD>(output.size()), &bytes_written, nullptr))
						break;
				}
			}
		}

		std::string handle_line(const std::string &line)
		{
			int id = 0;
			std::string method;
			if (!json_get_int(line, "id", id) || !json_get_raw_string(line, "method", method))
				return make_error(0, "bad_request", "Request must include numeric id and string method.");

			auto command = std::make_shared<control_command>();
			command->id = id;
			command->method = method;
			command->params = line;
			{
				std::lock_guard<std::mutex> lock(_queue_mutex);
				_queue.push_back(command);
			}
			std::unique_lock<std::mutex> lock(command->mutex);
			command->cv.wait(lock, [&]() { return command->done || !_running; });
			return command->done ? command->response : make_error(id, "stopped", "Control server stopped.");
		}

		std::string execute(control_command &command)
		{
			try
			{
				if (command.method == "get_runtime_info")
				{
					std::string preset = runtime_string([&](char *buffer, size_t *size) { _runtime->get_current_preset_path(buffer, size); });
					return make_response(command.id, "{\"width\":" + std::to_string(_width) + ",\"height\":" + std::to_string(_height) + ",\"presetPath\":" + json_string(preset) + ",\"effectsEnabled\":" + (_runtime->get_effects_state() ? "true" : "false") + ",\"outputPath\":" + json_string(path_utf8(_output_path)) + ",\"renderFps\":" + std::to_string(_stats != nullptr ? _stats->render_fps() : 0.0) + ",\"previewFps\":" + std::to_string(_stats != nullptr ? _stats->preview_fps() : 0.0) + ",\"sharedPreviewHandle\":" + std::to_string(reinterpret_cast<uintptr_t>(_shared_preview != nullptr ? _shared_preview->handle : nullptr)) + ",\"sharedPreviewWidth\":" + std::to_string(_shared_preview != nullptr ? _shared_preview->width : 0) + ",\"sharedPreviewHeight\":" + std::to_string(_shared_preview != nullptr ? _shared_preview->height : 0) + "}");
				}
				if (command.method == "list_state")
				{
					return make_response(command.id, "{\"runtime\":{\"width\":" + std::to_string(_width) + ",\"height\":" + std::to_string(_height) + ",\"effectsEnabled\":" + (_runtime->get_effects_state() ? "true" : "false") + ",\"renderFps\":" + std::to_string(_stats != nullptr ? _stats->render_fps() : 0.0) + ",\"previewFps\":" + std::to_string(_stats != nullptr ? _stats->preview_fps() : 0.0) + ",\"sharedPreviewHandle\":" + std::to_string(reinterpret_cast<uintptr_t>(_shared_preview != nullptr ? _shared_preview->handle : nullptr)) + ",\"sharedPreviewWidth\":" + std::to_string(_shared_preview != nullptr ? _shared_preview->width : 0) + ",\"sharedPreviewHeight\":" + std::to_string(_shared_preview != nullptr ? _shared_preview->height : 0) + "},\"techniques\":" + build_techniques_json(_runtime) + ",\"uniforms\":" + build_uniforms_json(_runtime) + ",\"preprocessorDefinitions\":" + build_preprocessor_definitions_json(_runtime) + "}");
				}
				if (command.method == "set_effects_state")
				{
					bool enabled = true;
					if (!json_get_bool(command.params, "enabled", enabled))
						return make_error(command.id, "bad_params", "Missing enabled.");
					_runtime->set_effects_state(enabled);
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "set_technique_state")
				{
					std::string id, effect_name, name;
					bool enabled = false;
					if (!json_get_raw_string(command.params, "id", id) || !split_effect_id(id, effect_name, name) || !json_get_bool(command.params, "enabled", enabled))
						return make_error(command.id, "bad_params", "Missing id or enabled.");
					auto technique = _runtime->find_technique(effect_name.c_str(), name.c_str());
					if (technique.handle == 0)
						return make_error(command.id, "not_found", "Technique not found.");
					_runtime->set_technique_state(technique, enabled);
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "set_uniform")
				{
					std::string id, effect_name, name;
					if (!json_get_raw_string(command.params, "id", id) || !split_effect_id(id, effect_name, name))
						return make_error(command.id, "bad_params", "Missing uniform id.");
					auto variable = _runtime->find_uniform_variable(effect_name.c_str(), name.c_str());
					if (variable.handle == 0)
						return make_error(command.id, "not_found", "Uniform not found.");
					reshade::api::format base_type = reshade::api::format::unknown;
					uint32_t rows = 0, columns = 0, array_length = 0;
					_runtime->get_uniform_variable_type(variable, &base_type, &rows, &columns, &array_length);
					const size_t count = std::max(1u, rows) * std::max(1u, columns);
					if (base_type == reshade::api::format::r32_float)
					{
						auto numbers = json_get_number_values(command.params, "value");
						if (numbers.empty()) return make_error(command.id, "bad_params", "Missing numeric value.");
						float values[16] = {};
						for (size_t i = 0; i < count && i < std::size(values); ++i) values[i] = static_cast<float>(numbers[std::min(i, numbers.size() - 1)]);
						_runtime->set_uniform_value_float(variable, values, count);
						_runtime->save_current_preset();
					}
					else if (base_type == reshade::api::format::r32_sint)
					{
						auto numbers = json_get_number_values(command.params, "value");
						if (numbers.empty()) return make_error(command.id, "bad_params", "Missing numeric value.");
						int32_t values[16] = {};
						for (size_t i = 0; i < count && i < std::size(values); ++i) values[i] = static_cast<int32_t>(numbers[std::min(i, numbers.size() - 1)]);
						_runtime->set_uniform_value_int(variable, values, count);
						_runtime->save_current_preset();
					}
					else if (base_type == reshade::api::format::r32_uint)
					{
						auto numbers = json_get_number_values(command.params, "value");
						if (numbers.empty()) return make_error(command.id, "bad_params", "Missing numeric value.");
						uint32_t values[16] = {};
						for (size_t i = 0; i < count && i < std::size(values); ++i) values[i] = static_cast<uint32_t>(std::max(0.0, numbers[std::min(i, numbers.size() - 1)]));
						_runtime->set_uniform_value_uint(variable, values, count);
						_runtime->save_current_preset();
					}
					else
					{
						bool value = false;
						if (!json_get_bool(command.params, "value", value))
						{
							auto numbers = json_get_number_values(command.params, "value");
							if (numbers.empty()) return make_error(command.id, "bad_params", "Missing bool value.");
							value = numbers[0] != 0.0;
						}
						bool values[16] = {};
						for (size_t i = 0; i < count && i < std::size(values); ++i) values[i] = value;
						_runtime->set_uniform_value_bool(variable, values, count);
						_runtime->save_current_preset();
					}
					return make_response(command.id, "{}");
				}
				if (command.method == "reset_uniform")
				{
					std::string id, effect_name, name;
					if (!json_get_raw_string(command.params, "id", id) || !split_effect_id(id, effect_name, name))
						return make_error(command.id, "bad_params", "Missing uniform id.");
					auto variable = _runtime->find_uniform_variable(effect_name.c_str(), name.c_str());
					if (variable.handle == 0)
						return make_error(command.id, "not_found", "Uniform not found.");
					_runtime->reset_uniform_value(variable);
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "reorder_techniques")
				{
					auto ids = json_get_string_array(command.params, "ids");
					std::vector<reshade::api::effect_technique> techniques;
					for (const std::string &id : ids)
					{
						std::string effect_name, name;
						if (!split_effect_id(id, effect_name, name))
							return make_error(command.id, "bad_params", "Invalid technique id.");
						auto technique = _runtime->find_technique(effect_name.c_str(), name.c_str());
						if (technique.handle == 0)
							return make_error(command.id, "not_found", "Technique not found.");
						techniques.push_back(technique);
					}
					_runtime->reorder_techniques(techniques.size(), techniques.data());
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "set_preprocessor_definition")
				{
					std::string effect_name, name, value;
					if (!json_get_raw_string(command.params, "name", name) || !json_get_raw_string(command.params, "value", value))
						return make_error(command.id, "bad_params", "Missing preprocessor name or value.");
					if (json_get_raw_string(command.params, "effectName", effect_name) && !effect_name.empty())
					{
						_runtime->set_preprocessor_definition_for_effect(effect_name.c_str(), name.c_str(), value.c_str());
						_runtime->reload_effect_next_frame(effect_name.c_str());
					}
					else
					{
						_runtime->set_preprocessor_definition(name.c_str(), value.c_str());
						_runtime->reload_effect_next_frame(nullptr);
					}
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "reload_effects")
				{
					std::string effect_name;
					_runtime->reload_effect_next_frame(json_get_raw_string(command.params, "effectName", effect_name) && !effect_name.empty() ? effect_name.c_str() : nullptr);
					return make_response(command.id, "{}");
				}
				if (command.method == "set_input_paths")
				{
					std::string color_path, depth_path, depth_format, output_path;
					if (!json_get_raw_string(command.params, "colorPath", color_path) ||
						!json_get_raw_string(command.params, "depthPath", depth_path) ||
						!json_get_raw_string(command.params, "depthFormat", depth_format) ||
						!json_get_raw_string(command.params, "outputPath", output_path))
						return make_error(command.id, "bad_params", "Missing colorPath, depthPath, depthFormat, or outputPath.");
					std::transform(depth_format.begin(), depth_format.end(), depth_format.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
					if (depth_format != "raw" && depth_format != "rgba")
						return make_error(command.id, "bad_params", "depthFormat must be raw or rgba.");
					if (!_input_switch)
						return make_error(command.id, "not_supported", "Input switching is not available.");
					std::filesystem::path output = std::filesystem::absolute(widen_utf8(output_path));
					const std::string error = _input_switch(std::filesystem::absolute(widen_utf8(color_path)), std::filesystem::absolute(widen_utf8(depth_path)), depth_format, output);
					if (!error.empty())
						return make_error(command.id, "input_load_failed", error);
					_output_path = std::move(output);
					return make_response(command.id, "{}");
				}
				if (command.method == "set_input_watch_enabled")
				{
					bool enabled = true;
					if (!json_get_bool(command.params, "enabled", enabled))
						return make_error(command.id, "bad_params", "Missing enabled.");
					if (!_input_watch)
						return make_error(command.id, "not_supported", "Input watch control is not available.");
					_input_watch(enabled);
					return make_response(command.id, "{}");
				}
				if (command.method == "save_preset")
				{
					_runtime->save_current_preset();
					return make_response(command.id, "{}");
				}
				if (command.method == "export_preset")
				{
					std::string path;
					if (!json_get_raw_string(command.params, "path", path) || path.empty())
						return make_error(command.id, "bad_params", "Missing export path.");
					_runtime->export_current_preset(path.c_str());
					return make_response(command.id, "{}");
				}
				if (command.method == "set_preset")
				{
					std::string path;
					if (!json_get_raw_string(command.params, "path", path) || path.empty())
						return make_error(command.id, "bad_params", "Missing preset path.");
					_runtime->set_current_preset_path(path.c_str());
					return make_response(command.id, "{}");
				}
				if (command.method == "save_screenshot")
				{
					if (_save_output)
					{
						const std::string error = _save_output();
						if (!error.empty())
							return make_error(command.id, "screenshot_failed", error);
					}
					else
					{
						_runtime->save_screenshot(nullptr);
					}
					return make_response(command.id, "{}");
				}
				return make_error(command.id, "unknown_method", "Unknown method.");
			}
			catch (const std::exception &ex)
			{
				return make_error(command.id, "exception", ex.what());
			}
		}

		std::string _pipe_name;
		reshade::api::effect_runtime *_runtime = nullptr;
		uint32_t _width = 0;
		uint32_t _height = 0;
		std::filesystem::path _output_path;
		const frame_rate_stats *_stats = nullptr;
		const shared_preview_state *_shared_preview = nullptr;
		input_switch_callback _input_switch;
		save_output_callback _save_output;
		input_watch_callback _input_watch;
		std::atomic_bool _running = false;
		std::thread _thread;
		std::mutex _queue_mutex;
		std::deque<std::shared_ptr<control_command>> _queue;
		HANDLE _pipe = INVALID_HANDLE_VALUE;
	};
	void bind_semantics(reshade::api::effect_runtime *runtime, ID3D11ShaderResourceView *color_srv, ID3D11ShaderResourceView *depth_srv, ID3D11ShaderResourceView *motion_srv)
	{
		const reshade::api::resource_view color_view = { reinterpret_cast<uint64_t>(color_srv) };
		const reshade::api::resource_view depth_view = { reinterpret_cast<uint64_t>(depth_srv) };
		const reshade::api::resource_view motion_view = { reinterpret_cast<uint64_t>(motion_srv) };

		for (const char *semantic : { "DEPTH" })
			runtime->update_texture_bindings(semantic, depth_view, depth_view);

		for (const char *semantic : { "PREVIOUS_COLOR", "PREV_COLOR", "PREV_FRAME", "HISTORY", "COLOR_HISTORY" })
			runtime->update_texture_bindings(semantic, color_view, color_view);

		for (const char *semantic : { "PREVIOUS_DEPTH", "PREV_DEPTH", "DEPTH_HISTORY" })
			runtime->update_texture_bindings(semantic, depth_view, depth_view);

		for (const char *semantic : { "MOTION", "MOTION_VECTOR", "MOTION_VECTORS", "VELOCITY" })
			runtime->update_texture_bindings(semantic, motion_view, motion_view);
	}
}

int wmain(int argc, wchar_t **argv)
{
	options opts;
	if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)))
	{
		std::cerr << "Failed to initialize COM.\n";
		return 1;
	}

	ComPtr<IWICImagingFactory> wic_factory;
	if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic_factory))))
	{
		std::cerr << "Failed to create WIC imaging factory.\n";
		CoUninitialize();
		return 1;
	}


	if (!parse_options(argc, argv, opts))
	{
		print_usage();
		return 1;
	}

	const std::filesystem::path default_input_dir = L"D:\\Program Files\\KoikatuSunshine\\UserData\\cap\\OfflineReShade";
	if (opts.color_path.empty())
		opts.color_path = default_input_dir / L"coloroutput.png";
	if (opts.depth_path.empty())
		opts.depth_path = default_input_dir / L"depthoutput.rfloat";
	if (opts.output_path.empty())
		opts.output_path = default_input_dir / L"reshadeoutput.png";
	if (opts.effect_dir.empty())
	{
		opts.effect_dir = prototype_directory() / L"Effects";
		std::filesystem::create_directories(opts.effect_dir);
	}

	if (!std::filesystem::exists(opts.color_path))
	{
		std::cerr << "Color image does not exist: " << path_utf8(opts.color_path) << '\n';
		return 1;
	}
	if (!std::filesystem::exists(opts.effect_dir))
	{
		std::cerr << "Effect directory does not exist: " << path_utf8(opts.effect_dir) << '\n';
		return 1;
	}

	image_rgba color_image;
	if (!load_png_rgba(wic_factory.Get(), opts.color_path, color_image))
	{
		std::cerr << "Failed to load color PNG: " << path_utf8(opts.color_path) << '\n';
		return 1;
	}

	const uint32_t width = opts.width != 0 ? opts.width : static_cast<uint32_t>(color_image.width);
	const uint32_t height = opts.height != 0 ? opts.height : static_cast<uint32_t>(color_image.height);
	color_image = resize_rgba(color_image, width, height);
	std::vector<float> depth_data;
	if (!load_depth_data(wic_factory.Get(), opts, width, height, depth_data))
	{
		std::cerr << "Failed to load depth " << opts.depth_format << ": " << path_utf8(opts.depth_path) << '\n';
		return 1;
	}
	const std::vector<float> motion_data(static_cast<size_t>(width) * height * 2, 0.0f);

	const std::filesystem::path work_dir = prototype_directory();
	const std::filesystem::path preset_path = make_preset(opts, work_dir);
	const std::filesystem::path config_path = make_config(opts, work_dir, preset_path);

	const HWND parent_hwnd = reinterpret_cast<HWND>(opts.parent_hwnd);
	HWND hwnd = create_render_window(width, height, parent_hwnd);
	const auto destroy_render_window = [&]() {
		if (hwnd != nullptr)
			DestroyWindow(hwnd);
	};
	if (hwnd == nullptr)
	{
		std::cerr << "Failed to create render window.\n";
		return 1;
	}

	ComPtr<ID3D11Device> device;
	ComPtr<ID3D11DeviceContext> context;
	ComPtr<IDXGISwapChain> swapchain;
	if (!create_device_and_swapchain(hwnd, width, height, device, context, swapchain))
	{
		std::cerr << "Failed to create D3D11 device and swapchain.\n";
		destroy_render_window();
		return 1;
	}

	ComPtr<ID3D11Texture2D> color_texture, depth_texture, motion_texture;
	ComPtr<ID3D11ShaderResourceView> color_srv, depth_srv, motion_srv;
	if (!create_shader_resource(device.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, width, height, color_image.pixels.data(), width * 4, color_texture, color_srv) ||
		!create_shader_resource(device.Get(), DXGI_FORMAT_R32_FLOAT, width, height, depth_data.data(), width * sizeof(float), depth_texture, depth_srv) ||
		!create_shader_resource(device.Get(), DXGI_FORMAT_R32G32_FLOAT, width, height, motion_data.data(), width * sizeof(float) * 2, motion_texture, motion_srv))
	{
		std::cerr << "Failed to create input shader resources.\n";
		destroy_render_window();
		return 1;
	}
	std::filesystem::file_time_type color_write_time = file_write_time_or_min(opts.color_path);
	std::filesystem::file_time_type depth_write_time = opts.depth_path.empty() ? std::filesystem::file_time_type::min() : file_write_time_or_min(opts.depth_path);

	reshade_exports reshade = load_reshade();
	if (reshade.module == nullptr)
	{
		std::cerr << "Failed to load ReShade64.dll from " << path_utf8(executable_directory()) << ", GetLastError=" << GetLastError() << '\n';
		destroy_render_window();
		return 1;
	}
	if (reshade.create_runtime == nullptr || reshade.destroy_runtime == nullptr || reshade.update_and_present_runtime == nullptr)
	{
		std::cerr << "Failed to load ReShade64.dll exports:"
			<< " create=" << (reshade.create_runtime != nullptr)
			<< " destroy=" << (reshade.destroy_runtime != nullptr)
			<< " update_present=" << (reshade.update_and_present_runtime != nullptr)
			<< '\n';
		FreeLibrary(reshade.module);
		destroy_render_window();
		return 1;
	}

	reshade::api::effect_runtime *runtime = nullptr;
	const std::string config_path_u8 = path_utf8(config_path);
	if (!reshade.create_runtime(reshade::api::device_api::d3d11, device.Get(), context.Get(), swapchain.Get(), config_path_u8.c_str(), &runtime) || runtime == nullptr)
	{
		std::cerr << "Failed to create ReShade effect runtime. Check ReShade.log next to ReShade64.dll.\n";
		FreeLibrary(reshade.module);
		destroy_render_window();
		return 1;
	}

	bind_semantics(runtime, color_srv.Get(), depth_srv.Get(), motion_srv.Get());
	frame_rate_stats stats;
	shared_preview_state shared_preview;
	if (opts.interactive && opts.preview_shared && !create_shared_preview_texture(device.Get(), width, height, shared_preview))
	{
		std::cerr << "Failed to create shared preview texture.\n";
		opts.preview_shared = false;
	}

	const auto load_input_paths = [&](const std::filesystem::path &new_color_path, const std::filesystem::path &new_depth_path, const std::string &new_depth_format) -> std::string {
		image_rgba new_color_image;
		if (!load_png_rgba(wic_factory.Get(), new_color_path, new_color_image))
			return "Failed to load color PNG: " + path_utf8(new_color_path);
		new_color_image = resize_rgba(new_color_image, width, height);

		std::vector<float> new_depth_data;
		options input_opts = opts;
		input_opts.depth_path = new_depth_path;
		input_opts.depth_format = new_depth_format;
		if (!load_depth_data(wic_factory.Get(), input_opts, width, height, new_depth_data))
			return "Failed to load depth " + new_depth_format + ": " + path_utf8(new_depth_path);

		ComPtr<ID3D11Texture2D> new_color_texture, new_depth_texture;
		ComPtr<ID3D11ShaderResourceView> new_color_srv, new_depth_srv;
		if (!create_shader_resource(device.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, width, height, new_color_image.pixels.data(), width * 4, new_color_texture, new_color_srv) ||
			!create_shader_resource(device.Get(), DXGI_FORMAT_R32_FLOAT, width, height, new_depth_data.data(), width * sizeof(float), new_depth_texture, new_depth_srv))
			return "Failed to create input shader resources.";

		color_image = std::move(new_color_image);
		depth_data = std::move(new_depth_data);
		color_texture = std::move(new_color_texture);
		depth_texture = std::move(new_depth_texture);
		color_srv = std::move(new_color_srv);
		depth_srv = std::move(new_depth_srv);
		opts.color_path = new_color_path;
		opts.depth_path = new_depth_path;
		opts.depth_format = new_depth_format;
		color_write_time = file_write_time_or_min(opts.color_path);
		depth_write_time = opts.depth_path.empty() ? std::filesystem::file_time_type::min() : file_write_time_or_min(opts.depth_path);
		bind_semantics(runtime, color_srv.Get(), depth_srv.Get(), motion_srv.Get());
		return {};
	};

	const auto reload_inputs_if_changed = [&]() {
		const std::filesystem::file_time_type new_color_write_time = file_write_time_or_min(opts.color_path);
		const std::filesystem::file_time_type new_depth_write_time = opts.depth_path.empty() ? std::filesystem::file_time_type::min() : file_write_time_or_min(opts.depth_path);
		if (new_color_write_time == color_write_time && new_depth_write_time == depth_write_time)
			return;

		if (!load_input_paths(opts.color_path, opts.depth_path, opts.depth_format).empty())
			return;
		std::cout << "INPUTS_RELOADED=1" << std::endl;
	};

	const auto switch_input_paths = [&](const std::filesystem::path &new_color_path, const std::filesystem::path &new_depth_path, const std::string &new_depth_format, const std::filesystem::path &new_output_path) -> std::string {
		const std::string error = load_input_paths(new_color_path, new_depth_path, new_depth_format);
		if (!error.empty())
			return error;
		opts.output_path = new_output_path;
		std::cout << "INPUTS_SWITCHED=1 " << path_utf8(opts.color_path) << std::endl;
		return {};
	};

	const auto save_current_output = [&]() -> std::string {
		std::vector<uint8_t> output = read_backbuffer(device.Get(), context.Get(), swapchain.Get(), width, height);
		if (output.empty())
			return "Failed to read backbuffer.";
		std::error_code ec;
		std::filesystem::create_directories(opts.output_path.parent_path(), ec);
		if (!save_png_rgba(wic_factory.Get(), opts.output_path, width, height, output))
			return "Failed to save output PNG: " + path_utf8(opts.output_path);
		std::cout << "Wrote " << path_utf8(opts.output_path) << " (" << width << "x" << height << ")\n";
		return {};
	};
	const auto set_input_watch_enabled = [&](bool enabled) {
		opts.disable_input_watch = !enabled;
		std::cout << "INPUT_WATCH=" << (enabled ? "1" : "0") << std::endl;
	};

	std::unique_ptr<control_server> control;
	if (opts.interactive)
	{
		if (opts.control_pipe.empty())
			opts.control_pipe = "OfflineReShade-" + std::to_string(GetCurrentProcessId());
		control = std::make_unique<control_server>(opts.control_pipe, runtime, width, height, opts.output_path, &stats, opts.preview_shared ? &shared_preview : nullptr, switch_input_paths, save_current_output, set_input_watch_enabled);
		control->start();
	}

	std::unique_ptr<preview_stream_server> preview_stream;
	const uint32_t preview_width = opts.preview_width != 0 ? opts.preview_width : width;
	const uint32_t preview_height = opts.preview_height != 0 ? opts.preview_height : height;
	if (opts.interactive && !opts.preview_pipe.empty())
	{
		preview_stream = std::make_unique<preview_stream_server>(opts.preview_pipe);
		preview_stream->start();
	}
	async_backbuffer_reader preview_reader;
	if (preview_stream != nullptr && !preview_reader.initialize(device.Get(), swapchain.Get(), width, height, 4))
	{
		std::cerr << "Failed to initialize async preview readback.\n";
		preview_stream.reset();
	}

	const HWND overlay_hwnd = reinterpret_cast<HWND>(opts.overlay_hwnd);
	ComPtr<IDXGISwapChain> overlay_swapchain;
	ComPtr<ID3D11RenderTargetView> overlay_rtv;
	uint32_t overlay_width = 0;
	uint32_t overlay_height = 0;

	const auto render_frame = [&](bool present) -> bool {
		if (!copy_rgba_to_backbuffer(context.Get(), swapchain.Get(), width, height, color_image.pixels))
			return false;

		if (present && overlay_hwnd != nullptr && reshade.set_external_overlay_target != nullptr)
		{
			if (!ensure_overlay_target(device.Get(), context.Get(), overlay_hwnd, overlay_swapchain, overlay_rtv, overlay_width, overlay_height))
				return false;

			const float clear_color[4] = { 0.08f, 0.085f, 0.095f, 1.0f };
			context->ClearRenderTargetView(overlay_rtv.Get(), clear_color);
			reshade.set_external_overlay_target(runtime, overlay_rtv.Get(), overlay_width, overlay_height);
		}
		else if (reshade.set_external_overlay_target != nullptr)
		{
			reshade.set_external_overlay_target(runtime, nullptr, 0, 0);
		}

		reshade.update_and_present_runtime(runtime);

		if (present && FAILED(swapchain->Present(0, 0)))
			return false;
		if (present && overlay_swapchain != nullptr && FAILED(overlay_swapchain->Present(0, 0)))
			return false;
		return true;
	};

	if (opts.interactive)
	{
		timer_resolution_scope timer_resolution;
		if (opts.preview_pipe.empty() && !opts.preview_shared)
			ShowWindow(hwnd, SW_SHOW);
		std::cout << "PREVIEW_HWND=" << reinterpret_cast<uintptr_t>(hwnd) << " PARENT_HWND=" << opts.parent_hwnd << std::endl;
		MSG msg = {};
		uint32_t input_reload_counter = 0;
		const auto target_frame_time = std::chrono::duration<double>(1.0 / 60.0);
		while (IsWindow(hwnd) && (parent_hwnd == nullptr || IsWindow(parent_hwnd)))
		{
			const auto frame_start = std::chrono::steady_clock::now();

			while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
			{
				if (msg.message == WM_QUIT)
					goto end_interactive;
				TranslateMessage(&msg);
				DispatchMessageW(&msg);
			}

			if (parent_hwnd != nullptr)
			{
				RECT parent_rect = {};
				if (IsWindowVisible(parent_hwnd) && GetWindowRect(parent_hwnd, &parent_rect))
				{
					SetWindowPos(hwnd, HWND_TOP,
						parent_rect.left,
						parent_rect.top,
						parent_rect.right - parent_rect.left,
						parent_rect.bottom - parent_rect.top,
						SWP_NOACTIVATE | SWP_SHOWWINDOW);
				}
				else
				{
					ShowWindow(hwnd, SW_HIDE);
				}
			}

			if (control != nullptr)
				control->process_pending();

			if (!opts.disable_input_watch && (++input_reload_counter % 10) == 0)
				reload_inputs_if_changed();

			if (!render_frame(true))
			{
				std::cerr << "Failed to upload color image to backbuffer.\n";
				reshade.destroy_runtime(runtime);
				FreeLibrary(reshade.module);
				destroy_render_window();
				return 1;
			}
			stats.mark_render_frame();
			if (preview_stream != nullptr)
			{
				std::vector<uint8_t> backbuffer = preview_reader.submit_and_try_read(context.Get(), swapchain.Get());
				if (!backbuffer.empty())
				{
					preview_stream->publish(preview_width, preview_height, make_preview_frame_bgra(backbuffer, width, height, preview_width, preview_height));
					stats.mark_preview_frame();
				}
			}
			else if (parent_hwnd != nullptr)
			{
				stats.mark_preview_frame();
			}
			else if (opts.preview_shared && shared_preview.texture != nullptr)
			{
				if (copy_backbuffer_to_texture(context.Get(), swapchain.Get(), shared_preview.texture.Get()))
					stats.mark_preview_frame();
			}

			const auto elapsed = std::chrono::steady_clock::now() - frame_start;
			if (elapsed < target_frame_time)
			{
				const auto target_time = frame_start + target_frame_time;
				auto remaining_ms = std::chrono::duration_cast<std::chrono::milliseconds>(target_time - std::chrono::steady_clock::now()).count();
				if (remaining_ms > 2)
					Sleep(static_cast<DWORD>(remaining_ms - 1));
				while (std::chrono::steady_clock::now() < target_time)
					Sleep(0);
			}
		}
	end_interactive:
		reshade.destroy_runtime(runtime);
		FreeLibrary(reshade.module);
		destroy_render_window();
		std::cout << "Preview stopped\n";
		wic_factory.Reset();
		CoUninitialize();
		return 0;
	}

	for (int frame = 0; frame < 120; ++frame)
	{
		if (!render_frame(false))
		{
			std::cerr << "Failed to upload color image to backbuffer.\n";
			reshade.destroy_runtime(runtime);
			FreeLibrary(reshade.module);
			destroy_render_window();
			return 1;
		}

		Sleep(16);
	}

	std::vector<uint8_t> output = read_backbuffer(device.Get(), context.Get(), swapchain.Get(), width, height);
	if (output.empty() || !save_png_rgba(wic_factory.Get(), opts.output_path, width, height, output))
	{
		std::cerr << "Failed to save output PNG: " << path_utf8(opts.output_path) << '\n';
		reshade.destroy_runtime(runtime);
		FreeLibrary(reshade.module);
		destroy_render_window();
		return 1;
	}

	reshade.destroy_runtime(runtime);
	FreeLibrary(reshade.module);
	destroy_render_window();

	std::cout << "Wrote " << path_utf8(opts.output_path) << " (" << width << "x" << height << ")\n";
	wic_factory.Reset();
	CoUninitialize();
	return 0;
}





















