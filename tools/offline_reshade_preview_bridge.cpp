#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <microsoft.ui.xaml.media.dxinterop.h>

#include <algorithm>
#include <cstdint>
#include <new>

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
	float2 uv = uv_transform.xy + input.uv * uv_transform.zw;
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
		return float4(0.0, 0.0, 0.0, 1.0);
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
		return bridge->panel->SetSwapChain(bridge->swapchain.Get());
	}

	HRESULT open_shared_texture(preview_bridge *bridge, uintptr_t shared_handle)
	{
		if (shared_handle == 0)
			return E_INVALIDARG;
		if (bridge->shared_handle == shared_handle && bridge->shared_srv != nullptr)
			return S_OK;

		bridge->shared_texture.Reset();
		bridge->shared_srv.Reset();
		HRESULT hr = bridge->device->OpenSharedResource(reinterpret_cast<HANDLE>(shared_handle), IID_PPV_ARGS(&bridge->shared_texture));
		if (FAILED(hr))
			return hr;
		hr = bridge->device->CreateShaderResourceView(bridge->shared_texture.Get(), nullptr, &bridge->shared_srv);
		if (FAILED(hr))
			return hr;
		bridge->shared_handle = shared_handle;
		return S_OK;
	}
}

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_Create(void *swap_chain_panel_unknown, uint32_t width, uint32_t height, void **out_bridge)
{
	if (swap_chain_panel_unknown == nullptr || out_bridge == nullptr)
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
		D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
		D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
		hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &bridge->device, &level, &bridge->context);
	}
	if (SUCCEEDED(hr))
		hr = create_swapchain(bridge);
	if (SUCCEEDED(hr))
		hr = create_shaders(bridge);

	if (FAILED(hr))
	{
		delete bridge;
		return hr;
	}

	*out_bridge = bridge;
	return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall ORPreview_Destroy(void *bridge_ptr)
{
	delete static_cast<preview_bridge *>(bridge_ptr);
}

extern "C" __declspec(dllexport) HRESULT __stdcall ORPreview_Resize(void *bridge_ptr, uint32_t width, uint32_t height)
{
	auto bridge = static_cast<preview_bridge *>(bridge_ptr);
	if (bridge == nullptr || bridge->swapchain == nullptr)
		return E_INVALIDARG;
	bridge->width = std::max(1u, width);
	bridge->height = std::max(1u, height);
	bridge->context->ClearState();
	return bridge->swapchain->ResizeBuffers(0, bridge->width, bridge->height, DXGI_FORMAT_UNKNOWN, 0);
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
	if (bridge == nullptr || bridge->swapchain == nullptr)
		return E_INVALIDARG;
	HRESULT hr = open_shared_texture(bridge, shared_handle);
	if (FAILED(hr))
		return hr;

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

	preview_constants constants = { origin_x, origin_y, scale_x, scale_y };
	bridge->context->UpdateSubresource(bridge->constants.Get(), 0, nullptr, &constants, 0, 0);

	ID3D11RenderTargetView *rtv_ptr = rtv.Get();
	ID3D11ShaderResourceView *srv_ptr = bridge->shared_srv.Get();
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
