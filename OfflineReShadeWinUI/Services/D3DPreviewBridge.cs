using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using OfflineReShade.Core;

namespace OfflineReShade.WinUI.Services;

public sealed class D3DPreviewBridge : IDisposable
{
    private IntPtr _bridge;
    private IntPtr _panelUnknown;
    private ulong _sharedHandle;
    private ulong _adapterLuid;
    private uint _sourceWidth;
    private uint _sourceHeight;
    private uint _width;
    private uint _height;
    private float _originX;
    private float _originY;
    private float _scaleX = 1.0f;
    private float _scaleY = 1.0f;

    public bool IsCreated => _bridge != IntPtr.Zero;

    public void EnsureCreated(SwapChainPanel panel, uint width, uint height, ulong adapterLuid)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_bridge != IntPtr.Zero && _adapterLuid != adapterLuid) Dispose();
        if (_bridge != IntPtr.Zero)
        {
            Resize(width, height);
            ThrowIfFailed(ORPreview_SetDisplaySize(_bridge, (float)Math.Max(1, panel.ActualWidth), (float)Math.Max(1, panel.ActualHeight)));
            return;
        }

        if (ORPreview_GetAbiVersion() != BuildContract.PreviewBridge)
            throw new InvalidOperationException("Incompatible preview bridge. Install all components from the same Offline ReShade package.");
        _panelUnknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try { ThrowIfFailed(ORPreview_Create(_panelUnknown, width, height, adapterLuid, out _bridge)); }
        catch { Dispose(); throw; }
        _adapterLuid = adapterLuid;
        _width = width;
        _height = height;
        ThrowIfFailed(ORPreview_SetDisplaySize(_bridge, (float)Math.Max(1, panel.ActualWidth), (float)Math.Max(1, panel.ActualHeight)));
    }

    public void Resize(uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);
        if (_bridge == IntPtr.Zero || (_width == width && _height == height))
            return;

        ThrowIfFailed(ORPreview_Resize(_bridge, width, height));
        _width = width;
        _height = height;
    }

    public void SetViewTransform(double originX, double originY, double scaleX, double scaleY)
    {
        _originX = (float)originX;
        _originY = (float)originY;
        _scaleX = (float)scaleX;
        _scaleY = (float)scaleY;
    }

    public void Render(ulong sharedHandle, uint sourceWidth, uint sourceHeight)
    {
        if (_bridge == IntPtr.Zero || sharedHandle == 0 || sourceWidth == 0 || sourceHeight == 0)
            return;

        _sharedHandle = sharedHandle;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        ThrowIfFailed(ORPreview_RenderShared(_bridge, (nuint)sharedHandle, sourceWidth, sourceHeight, _originX, _originY, _scaleX, _scaleY));
    }

    public void RenderLastFrame()
    {
        if (_sharedHandle != 0)
            Render(_sharedHandle, _sourceWidth, _sourceHeight);
    }

    public void Dispose()
    {
        _sharedHandle = 0;
        _sourceWidth = _sourceHeight = _width = _height = 0;
        if (_bridge != IntPtr.Zero)
        {
            ORPreview_Destroy(_bridge);
            _bridge = IntPtr.Zero;
        }
        if (_panelUnknown != IntPtr.Zero)
        {
            Marshal.Release(_panelUnknown);
            _panelUnknown = IntPtr.Zero;
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("OfflineReShadePreviewBridge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint ORPreview_GetAbiVersion();

    [DllImport("OfflineReShadePreviewBridge.dll", EntryPoint = "ORPreview_CreateV2", CallingConvention = CallingConvention.StdCall)]
    private static extern int ORPreview_Create(IntPtr swapChainPanelUnknown, uint width, uint height, ulong adapterLuid, out IntPtr bridge);

    [DllImport("OfflineReShadePreviewBridge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void ORPreview_Destroy(IntPtr bridge);

    [DllImport("OfflineReShadePreviewBridge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int ORPreview_Resize(IntPtr bridge, uint width, uint height);

    [DllImport("OfflineReShadePreviewBridge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int ORPreview_SetDisplaySize(IntPtr bridge, float logicalWidth, float logicalHeight);

    [DllImport("OfflineReShadePreviewBridge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int ORPreview_RenderShared(
        IntPtr bridge,
        nuint sharedHandle,
        uint sourceWidth,
        uint sourceHeight,
        float originX,
        float originY,
        float scaleX,
        float scaleY);
}
