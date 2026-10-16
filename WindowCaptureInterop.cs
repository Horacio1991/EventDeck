using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EventDeck;
internal static class WindowCaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAccess { IntPtr GetInterface(in Guid iid); }

    public static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = GraphicsCaptureItem.As<ICaptureItemInterop>();
        var ptr = factory.CreateForWindow(hwnd, new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"));
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var ptr = surface.As<IDxgiAccess>().GetInterface(typeof(ID3D11Texture2D).GUID);
        return new ID3D11Texture2D(ptr); // Owns the reference returned by GetInterface.
    }
    [DllImport("d3d11.dll", ExactSpelling=true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr graphicsDevice);
}
