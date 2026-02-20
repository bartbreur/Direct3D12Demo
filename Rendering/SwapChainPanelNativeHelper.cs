using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Direct3D12Demo;

public static class SwapChainPanelNativeHelper
{
    public static ISwapChainPanelNative GetNative(this SwapChainPanel panel)
    {
        // WinUI 3: use WinRT projection to get the COM interface
        return panel.As<ISwapChainPanelNative>();
    }
}
