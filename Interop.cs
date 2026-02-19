using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Direct3D12Demo;

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISwapChainPanelNative
{
    void SetSwapChain(IntPtr swapChain);
}

public static class SwapChainPanelNativeHelper
{
    public static ISwapChainPanelNative GetNative(SwapChainPanel panel)
    {
        // WinUI 3: use WinRT projection to get the COM interface
        return panel.As<ISwapChainPanelNative>();
    }
}
