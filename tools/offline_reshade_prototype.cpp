/*
 * Offline ReShade prototype
 * SPDX-License-Identifier: BSD-3-Clause
 */

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wincodec.h>
#include <wrl/client.h>

#include "reshade_api.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <regex>
#include <string>
#include <string_view>
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
		std::filesystem::path effect_dir;
		std::filesystem::path preset_path;
		std::filesystem::path output_path;
		uint32_t width = 0;
		uint32_t height = 0;
		uintptr_t parent_hwnd = 0;
		uintptr_t overlay_hwnd = 0;
		bool interactive = false;
	};

	struct reshade_exports
	{
		HMODULE module = nullptr;
		bool (*create_runtime)(reshade::api::device_api, void *, void *, void *, const char *, reshade::api::effect_runtime **) = nullptr;
		void (*destroy_runtime)(reshade::api::effect_runtime *) = nullptr;
		void (*update_and_present_runtime)(reshade::api::effect_runtime *) = nullptr;
		void (*set_external_overlay_target)(reshade::api::effect_runtime *, void *, uint32_t, uint32_t) = nullptr;
	};

	void print_usage()
	{
		std::cout <<
			"usage: OfflineReShadePrototype [--color <png>] [--depth <png>] [--effect-dir <dir>] [--preset <ini>] [--output <png>] [--width <w> --height <h>] [--parent-hwnd <hwnd>] [--overlay-hwnd <hwnd>] [--interactive]\n";
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
			else if (arg == L"--interactive")
			{
				opts.interactive = true;
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

	std::vector<float> make_depth_data(const image_rgba *depth_image, uint32_t width, uint32_t height)
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

		const std::vector<std::pair<std::string, std::string>> techniques = scan_techniques(opts.effect_dir);

		std::ofstream preset(preset_path, std::ios::binary);
		preset << "Techniques=";
		for (size_t i = 0; i < techniques.size(); ++i)
		{
			if (i != 0)
				preset << ',';
			preset << techniques[i].first << '@' << techniques[i].second;
		}
		preset << "\nTechniqueSorting=";
		for (size_t i = 0; i < techniques.size(); ++i)
		{
			if (i != 0)
				preset << ',';
			preset << techniques[i].first << '@' << techniques[i].second;
		}
		preset << "\n";
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

		const DWORD style = parent_hwnd != nullptr ? (WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN) : WS_OVERLAPPEDWINDOW;
		return CreateWindowExW(0, class_name, L"Offline ReShade Prototype", style,
			0, 0, static_cast<int>(width), static_cast<int>(height),
			parent_hwnd, nullptr, wc.hInstance, nullptr);
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
		opts.depth_path = default_input_dir / L"depthoutput.png";
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

	image_rgba depth_image;
	image_rgba *depth_image_ptr = nullptr;
	if (!opts.depth_path.empty())
	{
		if (!load_png_rgba(wic_factory.Get(), opts.depth_path, depth_image))
		{
			std::cerr << "Failed to load depth PNG: " << path_utf8(opts.depth_path) << '\n';
			return 1;
		}
		depth_image_ptr = &depth_image;
	}

	const uint32_t width = opts.width != 0 ? opts.width : static_cast<uint32_t>(color_image.width);
	const uint32_t height = opts.height != 0 ? opts.height : static_cast<uint32_t>(color_image.height);
	color_image = resize_rgba(color_image, width, height);
	const std::vector<float> depth_data = make_depth_data(depth_image_ptr, width, height);
	const std::vector<float> motion_data(static_cast<size_t>(width) * height * 2, 0.0f);

	const std::filesystem::path work_dir = prototype_directory();
	const std::filesystem::path preset_path = make_preset(opts, work_dir);
	const std::filesystem::path config_path = make_config(opts, work_dir, preset_path);

	HWND hwnd = create_render_window(width, height, reinterpret_cast<HWND>(opts.parent_hwnd));
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
		DestroyWindow(hwnd);
		return 1;
	}

	ComPtr<ID3D11Texture2D> color_texture, depth_texture, motion_texture;
	ComPtr<ID3D11ShaderResourceView> color_srv, depth_srv, motion_srv;
	if (!create_shader_resource(device.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, width, height, color_image.pixels.data(), width * 4, color_texture, color_srv) ||
		!create_shader_resource(device.Get(), DXGI_FORMAT_R32_FLOAT, width, height, depth_data.data(), width * sizeof(float), depth_texture, depth_srv) ||
		!create_shader_resource(device.Get(), DXGI_FORMAT_R32G32_FLOAT, width, height, motion_data.data(), width * sizeof(float) * 2, motion_texture, motion_srv))
	{
		std::cerr << "Failed to create input shader resources.\n";
		DestroyWindow(hwnd);
		return 1;
	}

	reshade_exports reshade = load_reshade();
	if (reshade.module == nullptr)
	{
		std::cerr << "Failed to load ReShade64.dll from " << path_utf8(executable_directory()) << ", GetLastError=" << GetLastError() << '\n';
		DestroyWindow(hwnd);
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
		DestroyWindow(hwnd);
		return 1;
	}

	reshade::api::effect_runtime *runtime = nullptr;
	const std::string config_path_u8 = path_utf8(config_path);
	if (!reshade.create_runtime(reshade::api::device_api::d3d11, device.Get(), context.Get(), swapchain.Get(), config_path_u8.c_str(), &runtime) || runtime == nullptr)
	{
		std::cerr << "Failed to create ReShade effect runtime. Check ReShade.log next to ReShade64.dll.\n";
		FreeLibrary(reshade.module);
		DestroyWindow(hwnd);
		return 1;
	}

	bind_semantics(runtime, color_srv.Get(), depth_srv.Get(), motion_srv.Get());

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
		runtime->open_overlay(true, reshade::api::input_source::keyboard);
		ShowWindow(hwnd, SW_SHOW);
		MSG msg = {};
		while (IsWindow(hwnd) && (opts.parent_hwnd == 0 || IsWindow(reinterpret_cast<HWND>(opts.parent_hwnd))))
		{
			while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
			{
				if (msg.message == WM_QUIT)
					goto end_interactive;
				TranslateMessage(&msg);
				DispatchMessageW(&msg);
			}

			if (opts.parent_hwnd != 0)
			{
				RECT parent_rect = {};
				if (GetClientRect(reinterpret_cast<HWND>(opts.parent_hwnd), &parent_rect))
					SetWindowPos(hwnd, nullptr, 0, 0, parent_rect.right - parent_rect.left, parent_rect.bottom - parent_rect.top, SWP_NOZORDER | SWP_NOACTIVATE);
			}

			if (!render_frame(true))
			{
				std::cerr << "Failed to upload color image to backbuffer.\n";
				reshade.destroy_runtime(runtime);
				FreeLibrary(reshade.module);
				DestroyWindow(hwnd);
				return 1;
			}
			Sleep(16);
		}
	end_interactive:
		reshade.destroy_runtime(runtime);
		FreeLibrary(reshade.module);
		DestroyWindow(hwnd);
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
			DestroyWindow(hwnd);
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
		DestroyWindow(hwnd);
		return 1;
	}

	reshade.destroy_runtime(runtime);
	FreeLibrary(reshade.module);
	DestroyWindow(hwnd);

	std::cout << "Wrote " << path_utf8(opts.output_path) << " (" << width << "x" << height << ")\n";
	wic_factory.Reset();
	CoUninitialize();
	return 0;
}





















