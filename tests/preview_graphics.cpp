// Exercise the production D3D bridge with an offscreen composition panel.
#include "../tools/offline_reshade_preview_bridge.cpp"
#include <iostream>
#include <vector>
#include <stdexcept>
#include <limits>
#include <string>

static void check(bool value, const char *message) { if (!value) throw std::runtime_error(message); }
struct test_panel : ISwapChainPanelNative
{
    ULONG references = 1;
    ComPtr<IDXGISwapChain> attached;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(ISwapChainPanelNative)) return E_NOINTERFACE;
        *out = static_cast<ISwapChainPanelNative *>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { return --references; }
    HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain *value) override { attached = value; return S_OK; }
};

int main(int argc, char **argv)
{
    try {
        // Sample a real host's shared texture through the production bridge, without a window.
        if (argc == 6 && std::string(argv[1]) == "--sample-shared") {
            const auto luid = std::stoull(argv[2]), handle = std::stoull(argv[3]);
            const auto width = std::stoul(argv[4]), height = std::stoul(argv[5]);
            test_panel panel; void *opaque = nullptr;
            check(SUCCEEDED(ORPreview_CreateV2(&panel, 32, 32, luid, &opaque)), "sample bridge creation failed");
            auto bridge = static_cast<preview_bridge *>(opaque);
            // Several frames release the producer after any old queued image.
            for (int i = 0; i < 12; ++i) {
                check(SUCCEEDED(ORPreview_RenderShared(opaque, handle, width, height, 0, 0, 32, 32)), "sample render failed");
                Sleep(20);
            }
            check(bridge->has_frame, "host did not publish a shared frame");
            ComPtr<ID3D11Texture2D> target, staging;
            check(SUCCEEDED(bridge->swapchain->GetBuffer(1, IID_PPV_ARGS(&target))), "sample target failed");
            D3D11_TEXTURE2D_DESC desc = {}; target->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = desc.MiscFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            check(SUCCEEDED(bridge->device->CreateTexture2D(&desc, nullptr, &staging)), "sample allocation failed");
            bridge->context->CopyResource(staging.Get(), target.Get());
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            check(SUCCEEDED(bridge->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)), "sample map failed");
            const auto p = static_cast<uint8_t *>(mapped.pData) + 16 * mapped.RowPitch + 16 * 4;
            std::cout << ((p[2] << 16) | (p[1] << 8) | p[0]) << '\n';
            bridge->context->Unmap(staging.Get(), 0); ORPreview_Destroy(opaque); return 0;
        }
        ComPtr<ID3D11Device> producer;
        ComPtr<ID3D11DeviceContext> context;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &producer, nullptr, &context);
        if (FAILED(hr)) hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &producer, nullptr, &context);
        check(SUCCEEDED(hr), "producer creation failed");
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC adapter_desc = {};
        check(SUCCEEDED(producer.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapter_desc)), "adapter identity failed");
        uint64_t luid = (static_cast<uint64_t>(static_cast<uint32_t>(adapter_desc.AdapterLuid.HighPart)) << 32) | adapter_desc.AdapterLuid.LowPart;
        test_panel panel;
        void *opaque = nullptr;
        check(FAILED(ORPreview_CreateV2(&panel, 320, 240, UINT64_MAX, &opaque)) && opaque == nullptr, "unknown adapter silently selected a default GPU");
        check(SUCCEEDED(ORPreview_CreateV2(&panel, 320, 240, luid, &opaque)), "same-adapter bridge creation failed");
        auto bridge = static_cast<preview_bridge *>(opaque);
        check(SUCCEEDED(ORPreview_SetDisplaySize(opaque, 256, 192)), "DPI composition transform failed");
        ComPtr<IDXGISwapChain2> swapchain2; DXGI_MATRIX_3X2_F transform = {};
        check(SUCCEEDED(bridge->swapchain.As(&swapchain2)) && SUCCEEDED(swapchain2->GetMatrixTransform(&transform)) &&
            std::abs(transform._11 - .8f) < .0001f && std::abs(transform._22 - .8f) < .0001f, "DPI was applied twice");

        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = 64; desc.Height = 32; desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        ComPtr<ID3D11Texture2D> shared;
        check(SUCCEEDED(producer->CreateTexture2D(&desc, nullptr, &shared)), "shared texture creation failed");
        ComPtr<IDXGIKeyedMutex> mutex; ComPtr<IDXGIResource> resource;
        HANDLE handle = nullptr;
        check(SUCCEEDED(shared.As(&mutex)) && SUCCEEDED(shared.As(&resource)) && SUCCEEDED(resource->GetSharedHandle(&handle)), "shared handle/mutex unavailable");
        std::vector<uint8_t> pixels(64 * 32 * 4);
        auto publish = [&](bool blue) {
            check(mutex->AcquireSync(0, 1000) == S_OK, "producer did not regain the shared resource");
            for (size_t i = 0; i < pixels.size(); i += 4) { pixels[i] = blue ? 0 : 255; pixels[i+1] = 0; pixels[i+2] = blue ? 255 : 0; pixels[i+3] = 255; }
            context->UpdateSubresource(shared.Get(), 0, nullptr, pixels.data(), 64 * 4, 0);
            context->Flush(); check(SUCCEEDED(mutex->ReleaseSync(1)), "producer release failed");
            // Wait only in the harness; production skips a busy shared frame without blocking UI.
            ComPtr<ID3D11Texture2D> consumer_texture;
            ComPtr<IDXGIKeyedMutex> consumer_mutex;
            check(SUCCEEDED(bridge->device->OpenSharedResource(handle, IID_PPV_ARGS(&consumer_texture))) && SUCCEEDED(consumer_texture.As(&consumer_mutex)), "consumer wait open failed");
            check(consumer_mutex->AcquireSync(1, 1000) == S_OK && SUCCEEDED(consumer_mutex->ReleaseSync(1)), "producer GPU work did not complete");
        };
        auto render_pixel = [&](float x, float y, float w, float h, uint32_t px, uint32_t py) {
            ComPtr<ID3D11Texture2D> target;
            check(SUCCEEDED(bridge->swapchain->GetBuffer(0, IID_PPV_ARGS(&target))), "backbuffer failed");
            check(SUCCEEDED(ORPreview_RenderShared(opaque, reinterpret_cast<uintptr_t>(handle), 64, 32, x, y, w, h)), "render failed");
            target.Reset();
            check(SUCCEEDED(bridge->swapchain->GetBuffer(1, IID_PPV_ARGS(&target))), "presented backbuffer failed");
            D3D11_TEXTURE2D_DESC read_desc = {}; target->GetDesc(&read_desc);
            read_desc.Usage = D3D11_USAGE_STAGING; read_desc.BindFlags = read_desc.MiscFlags = 0; read_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;
            check(SUCCEEDED(bridge->device->CreateTexture2D(&read_desc, nullptr, &staging)), "readback allocation failed");
            bridge->context->CopyResource(staging.Get(), target.Get());
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            check(SUCCEEDED(bridge->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)), "readback failed");
            const auto p = static_cast<uint8_t *>(mapped.pData) + py * mapped.RowPitch + px * 4;
            uint32_t rgb = (p[2] << 16) | (p[1] << 8) | p[0];
            bridge->context->Unmap(staging.Get(), 0); return rgb;
        };
        publish(false);
        auto first = render_pixel(0, 40, 320, 160, 160, 120);
        check(first == 0xFF0000, "fitted preview is incorrect");
        check(render_pixel(0, 40, 320, 160, 160, 10) == 0, "letterbox sampled stretched edge pixels");
        check(render_pixel(-70000, -50000, 131072, 90000, 160, 120) == 0xFF0000, "extreme zoom/pan exceeded viewport limits");
        check(FAILED(ORPreview_RenderShared(opaque, reinterpret_cast<uintptr_t>(handle), 64, 32, std::numeric_limits<float>::quiet_NaN(), 0, 320, 240)), "NaN geometry was accepted");
        check(FAILED(ORPreview_Resize(opaque, 16384, 16384)) && bridge->width == 320, "oversized surface changed live state");
        for (int i = 0; i < 80; ++i) {
            publish(i % 2 != 0);
            check(render_pixel(0, 0, 320, 240, 160, 120) == (i % 2 ? 0x0000FF : 0xFF0000), "shared preview read stale or torn content");
        }
        check(SUCCEEDED(ORPreview_Resize(opaque, 640, 360)), "DPI surface resize failed");
        check(render_pixel(0, 0, 640, 360, 320, 180) == 0x0000FF, "resize lost cached pixels");
        check(SUCCEEDED(bridge->device->GetDeviceRemovedReason()), "invalid drawing commands removed the device");
        ORPreview_Destroy(opaque);
        check(!panel.attached && panel.references == 1, "preview teardown leaked its panel/swapchain");
        std::cout << "PASS: same-adapter D3D sharing, 80 synchronized frames, cached repaint, extreme zoom, letterboxing, invalid geometry, DPI resize and teardown.\n";
        return 0;
    } catch (const std::exception &ex) { std::cerr << ex.what() << '\n'; return 1; }
}
