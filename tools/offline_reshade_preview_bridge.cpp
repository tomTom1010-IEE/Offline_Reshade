#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <microsoft.ui.xaml.media.dxinterop.h>

#include <algorithm>
#include <cstdint>
#include <new>
#include <cmath>
#include "offline_render_limits.hpp"
#include "../include/offline_contract.hpp"

using Microsoft::WRL::ComPtr;

namespace
{
	struct preview_bridge
	{
		ComPtr<ID3D11Device> device;
		ComPtr<ID3D11DeviceContext> context;
		ComPtr<IDXGISwapChain1> swapchain;
		ComPtr<ISwapChainPanelNative> panel;
		ComPtr<ID3D11VertexShader> vertex_shader;
		ComPtr<ID3D11PixelShader> pixel_shader;
		ComPtr<ID3D11Buffer> constants;
		ComPtr<ID3D11SamplerState> sampler;
		ComPtr<ID3D11Texture2D> shared_texture;
		ComPtr<ID3D11ShaderResourceView> shared_srv;
		ComPtr<IDXGIKeyedMutex> shared_mutex;
		ComPtr<ID3D11Texture2D> cached_texture;
		ComPtr<ID3D11ShaderResourceView> cached_srv;
		bool has_frame = false;
		uintptr_t shared_handle = 0;
		uint32_t width = 1;
		uint32_t height = 1;
	};

	struct preview_constants
	{
		float origin_x;
		float origin_y;
		float scale_x;
		float scale_y;
	};

	const char g_shader_source[] = R"(
Texture2D source_texture : register(t0);
SamplerState source_sampler : register(s0);

cbuffer PreviewConstants : register(b0)
{
	float4 uv_transform;
};

struct VSOut
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

VSOut vs_main(uint vertex_id : SV_VertexID)
{
	float2 positions[3] = { float2(-1.0, -1.0), float2(-1.0, 3.0), float2(3.0, -1.0) };
	float2 uvs[3] = { float2(0.0, 1.0), float2(0.0, -1.0), float2(2.0, 1.0) };
	VSOut output;
	output.pos = float4(positions[vertex_id], 0.0, 1.0);
	output.uv = uvs[vertex_id];
	return output;
}

float4 ps_main(VSOut input) : SV_TARGET
{
	float2 uv = input.uv * uv_transform.zw + uv_transform.xy;
	if (any(uv < 0.0) || any(uv > 1.0)) return float4(0, 0, 0, 1);
	return source_texture.Sample(source_sampler, uv);
}
)";

	HRESULT create_shaders(preview_bridge *bridge)
	{
		ComPtr<ID3DBlob> vs_blob, ps_blob, errors;
		HRESULT hr = D3DCompile(g_shader_source, sizeof(g_shader_source) - 1, nullptr, nullptr, nullptr, "vs_main", "vs_4_0", 0, 0, &vs_blob, &errors);
		if (FAILED(hr))
			return hr;
		hr = D3DCompile(g_shader_source, sizeof(g_shader_source) - 1, nullptr, nullptr, nullptr, "ps_main", "ps_4_0", 0, 0, &ps_blob, &errors);
		if (FAILED(hr))
			return hr;
		hr = bridge->device->CreateVertexShader(vs_blob->GetBufferPointer(), vs_blob->GetBufferSize(), nullptr, &bridge->vertex_shader);
		if (FAILED(hr))
			return hr;
		hr = bridge->device->CreatePixelShader(ps_blob->GetBufferPointer(), ps_blob->GetBufferSize(), nullptr, &bridge->pixel_shader);
		if (FAILED(hr))
			return hr;

		D3D11_SAMPLER_DESC sampler_desc = {};
		sampler_desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
		sampler_desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
		sampler_desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
		sampler_desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
		sampler_desc.MaxLOD = D3D11_FLOAT32_MAX;
		hr = bridge->device->CreateSamplerState(&sampler_desc, &bridge->sampler);
		if (FAILED(hr))
			return hr;

		D3D11_BUFFER_DESC constant_desc = {};
		constant_desc.ByteWidth = sizeof(preview_constants);
		constant_desc.Usage = D3D11_USAGE_DEFAULT;
		constant_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
		return bridge->device->CreateBuffer(&constant_desc, nullptr, &bridge->constants);
	}

	HRESULT create_swapchain(preview_bridge *bridge)
	{
		ComPtr<IDXGIDevice> dxgi_device;
		ComPtr<IDXGIAdapter> adapter;
		ComPtr<IDXGIFactory2> factory;
		HRESULT hr = bridge->device.As(&dxgi_device);
		if (FAILED(hr))
			return hr;
		hr = dxgi_device->GetAdapter(&adapter);
		if (FAILED(hr))
			return hr;
		hr = adapter->GetParent(IID_PPV_ARGS(&factory));
		if (FAILED(hr))
			return hr;

		DXGI_SWAP_CHAIN_DESC1 desc = {};
		desc.Width = bridge->width;
		desc.Height = bridge->height;
		desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
		desc.Stereo = FALSE;
		desc.SampleDesc.Count = 1;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.BufferCount = 2;
		desc.Scaling = DXGI_SCALING_STRETCH;
		desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
		desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

		hr = factory->CreateSwapChainForComposition(bridge->device.Get(), &desc, nullptr, &bridge->swapchain);
		if (FAILED(hr))
			return hr;
		return bridge->panel != nullptr ? bridge->panel->SetSwapChain(bridge->swapchain.Get()) : S_OK;
	}

	HRESULT open_shared_texture(preview_bridge *bridge, uintptr_t shared_handle)
	{
		if (shared_handle == 0)
			return E_INVALIDARG;
		if (bridge->shared_handle == shared_handle && bridge->shared_srv != nullptr)
			return S_OK;

		bridge->shared_texture.Reset();
		bridge->shared_srv.Reset();
		bridge->shared_mutex.Reset();
		bridge->cached_texture.Reset();
		bridge->cached_srv.Reset();
		bridge->has_frame = false;
		HRESULT hr = bridge->device->OpenSharedResource(reinterpret_cast<HANDLE>(shared_handle), IID_PPV_ARGS(&bridge->shared_texture));
		if (FAILED(hr))
			return hr;
		if (FAILED(bridge->shared_texture.As(&bridge->shared_mutex))) return E_NOINTERFACE;
		hr = bridge->device->CreateShaderResourceView(bridge->shared_texture.Get(), nullptr, &bridge->shared_srv);
		if (FAILED(hr))
			return hr;
		D3D11_TEXTURE2D_DESC desc = {};
		bridge->shared_texture->GetDesc(&desc);
		if (!offline_limits::image_size(desc.Width, desc.Height)) return E_INVALIDARG;
		desc.MiscFlags = 0;
		desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		hr = bridge->device->CreateTexture2D(&desc, nullptr, &bridge->cached_texture);
		if (SUCCEEDED(hr)) hr = bridge->device->CreateShaderResourceView(bridge->cached_texture.Get(), nullptr, &bridge->cached_srv);
		if (FAILED(hr)) return hr;
		bridge->shared_handle = shared_handle;
		return S_OK;
	}
}

extern "C" __declspec(dllexport) uint32_t __stdcall ORPreview_GetAbiVersion() { return offline_contract::PreviewBridge; }

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_CreateV2(void *swap_chain_panel_unknown, uint32_t width, uint32_t height, uint64_t adapter_luid, void **out_bridge)
{
	if (swap_chain_panel_unknown == nullptr || out_bridge == nullptr || !offline_limits::preview_size(width, height))
		return E_INVALIDARG;
	*out_bridge = nullptr;

	auto bridge = new (std::nothrow) preview_bridge();
	if (bridge == nullptr)
		return E_OUTOFMEMORY;
	bridge->width = std::max(1u, width);
	bridge->height = std::max(1u, height);

	HRESULT hr = static_cast<IUnknown *>(swap_chain_panel_unknown)->QueryInterface(IID_PPV_ARGS(&bridge->panel));
	if (SUCCEEDED(hr))
	{
		ComPtr<IDXGIFactory4> factory;
		ComPtr<IDXGIAdapter> adapter;
		LUID luid = { static_cast<DWORD>(adapter_luid), static_cast<LONG>(adapter_luid >> 32) };
		hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
		if (SUCCEEDED(hr)) hr = factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter));
		D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
		D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
		if (SUCCEEDED(hr)) hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &bridge->device, &level, &bridge->context);
	}
	if (SUCCEEDED(hr))
		hr = create_swapchain(bridge);
	if (SUCCEEDED(hr))
		hr = create_shaders(bridge);

	if (FAILED(hr))
	{
		if (bridge->panel != nullptr) bridge->panel->SetSwapChain(nullptr);
		delete bridge;
		return hr;
	}

	*out_bridge = bridge;
	return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall ORPreview_Destroy(void *bridge_ptr)
{
	auto bridge = static_cast<preview_bridge *>(bridge_ptr);
	if (bridge != nullptr && bridge->panel != nullptr) bridge->panel->SetSwapChain(nullptr);
	delete bridge;
}

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_Resize(void *bridge_ptr, uint32_t width, uint32_t height)
{
	auto bridge = static_cast<preview_bridge *>(bridge_ptr);
	if (bridge == nullptr || bridge->swapchain == nullptr || !offline_limits::preview_size(width, height))
		return E_INVALIDARG;
	bridge->context->ClearState();
	const HRESULT hr = bridge->swapchain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
	if (SUCCEEDED(hr)) { bridge->width = width; bridge->height = height; }
	return hr;
}

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_SetDisplaySize(void *bridge_ptr, float logical_width, float logical_height)
{
	auto bridge = static_cast<preview_bridge *>(bridge_ptr);
	if (bridge == nullptr || !std::isfinite(logical_width) || !std::isfinite(logical_height) || logical_width <= 0 || logical_height <= 0) return E_INVALIDARG;
	ComPtr<IDXGISwapChain2> swapchain2;
	HRESULT hr = bridge->swapchain.As(&swapchain2);
	if (FAILED(hr)) return hr;
	// SwapChainPanel uses DIPs; its composition scale must not apply DPI a second time.
	DXGI_MATRIX_3X2_F transform = { logical_width / bridge->width, 0, 0, logical_height / bridge->height, 0, 0 };
	return swapchain2->SetMatrixTransform(&transform);
}

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_RenderShared(
	void *bridge_ptr,
	uintptr_t shared_handle,
	uint32_t source_width,
	uint32_t source_height,
	float origin_x,
	float origin_y,
	float scale_x,
	float scale_y)
{
	auto bridge = static_cast<preview_bridge *>(bridge_ptr);
	if (bridge == nullptr || bridge->swapchain == nullptr || !offline_limits::image_size(source_width, source_height) ||
		!std::isfinite(origin_x) || !std::isfinite(origin_y) || !std::isfinite(scale_x) || !std::isfinite(scale_y) || scale_x <= 0 || scale_y <= 0)
		return E_INVALIDARG;
	if (FAILED(bridge->device->GetDeviceRemovedReason())) return bridge->device->GetDeviceRemovedReason();
	HRESULT hr = open_shared_texture(bridge, shared_handle);
	if (FAILED(hr))
		return hr;

	hr = bridge->shared_mutex->AcquireSync(1, 0);
	if (hr == WAIT_ABANDONED) return DXGI_ERROR_DEVICE_RESET;
	if (hr == S_OK) {
		bridge->context->CopyResource(bridge->cached_texture.Get(), bridge->shared_texture.Get());
		bridge->context->Flush();
		hr = bridge->shared_mutex->ReleaseSync(0);
		if (FAILED(hr)) return hr;
		bridge->has_frame = true;
	} else if (hr != WAIT_TIMEOUT) return hr;
	if (!bridge->has_frame) return S_FALSE;

	ComPtr<ID3D11Texture2D> backbuffer;
	ComPtr<ID3D11RenderTargetView> rtv;
	hr = bridge->swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer));
	if (FAILED(hr))
		return hr;
	hr = bridge->device->CreateRenderTargetView(backbuffer.Get(), nullptr, &rtv);
	if (FAILED(hr))
		return hr;

	const float clear_color[4] = { 0, 0, 0, 1 };
	bridge->context->ClearRenderTargetView(rtv.Get(), clear_color);

	D3D11_VIEWPORT viewport = {};
	viewport.Width = static_cast<float>(bridge->width);
	viewport.Height = static_cast<float>(bridge->height);
	viewport.MinDepth = 0.0f;
	viewport.MaxDepth = 1.0f;

	// Zoom changes texture coordinates, never the D3D viewport bounds.
	preview_constants constants = { -origin_x / scale_x, -origin_y / scale_y, bridge->width / scale_x, bridge->height / scale_y };
	bridge->context->UpdateSubresource(bridge->constants.Get(), 0, nullptr, &constants, 0, 0);

	ID3D11RenderTargetView *rtv_ptr = rtv.Get();
	ID3D11ShaderResourceView *srv_ptr = bridge->cached_srv.Get();
	ID3D11SamplerState *sampler_ptr = bridge->sampler.Get();
	ID3D11Buffer *constant_ptr = bridge->constants.Get();
	bridge->context->OMSetRenderTargets(1, &rtv_ptr, nullptr);
	bridge->context->RSSetViewports(1, &viewport);
	bridge->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	bridge->context->VSSetShader(bridge->vertex_shader.Get(), nullptr, 0);
	bridge->context->PSSetShader(bridge->pixel_shader.Get(), nullptr, 0);
	bridge->context->PSSetShaderResources(0, 1, &srv_ptr);
	bridge->context->PSSetSamplers(0, 1, &sampler_ptr);
	bridge->context->PSSetConstantBuffers(0, 1, &constant_ptr);
	bridge->context->Draw(3, 0);
	srv_ptr = nullptr;
	bridge->context->PSSetShaderResources(0, 1, &srv_ptr);
	return bridge->swapchain->Present(0, 0);
}
