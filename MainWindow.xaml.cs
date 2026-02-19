using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Direct3D12Demo;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Direct3D12Demo;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const uint FrameCount = 2;

    private IDXGIFactory4 _dxgiFactory;
    private ID3D12Device _device;
    private ID3D12CommandQueue _commandQueue;
    private IDXGISwapChain3 _swapChain;
    private ID3D12DescriptorHeap _rtvHeap;
    private ID3D12Resource[] _renderTargets = new ID3D12Resource[FrameCount];
    private ID3D12CommandAllocator _commandAllocator;
    private ID3D12GraphicsCommandList _commandList;
    private ID3D12Fence _fence;
    private ulong _fenceValue;
    private IntPtr _fenceEvent;
    private uint _rtvDescriptorSize;
    private uint _frameIndex;

    public MainWindow()
    {
        InitializeComponent();

        Activated += MainWindow_Activated;
        DxPanel.SizeChanged += DxPanel_SizeChanged;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= MainWindow_Activated;

        await InitializeD3DAsync();
        StartRenderLoop();
    }

    private async Task InitializeD3DAsync()
    {
        // Create DXGI factory
        _dxgiFactory = CreateDXGIFactory2<IDXGIFactory4>(false);

        // Create device
        _device = D3D12CreateDevice<ID3D12Device>(null, Vortice.Direct3D.FeatureLevel.Level_11_0);

        // Create command queue
        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        _commandQueue = _device.CreateCommandQueue(queueDesc);

        // Create swap chain for SwapChainPanel
        CreateSwapChainForPanel();

        // Create descriptor heap for RTVs
        var rtvHeapDesc = new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView,
            FrameCount,
            DescriptorHeapFlags.None);

        _rtvHeap = _device.CreateDescriptorHeap(rtvHeapDesc);
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        // Create frame resources
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

        for (uint i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);

            // Advance by one descriptor
            rtvHandle = rtvHandle.Offset(1, _rtvDescriptorSize);
        }


        // Command allocator & list
        _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            0, CommandListType.Direct, _commandAllocator, null);
        _commandList.Close();

        // Fence
        _fence = _device.CreateFence(0);
        _fenceValue = 1;
        _fenceEvent = CreateEventEx(IntPtr.Zero, null, 0, 0);

        _frameIndex = _swapChain.CurrentBackBufferIndex;

        await Task.CompletedTask;
    }

    private void CreateSwapChainForPanel()
    {
        // Get panel size
        uint width = (uint)Math.Max(1, DxPanel.ActualWidth);
        uint height = (uint)Math.Max(1, DxPanel.ActualHeight);

        var swapChainDesc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = FrameCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        using var tempSwapChain = _dxgiFactory.CreateSwapChainForComposition(
            _commandQueue,
            swapChainDesc);

        _swapChain = tempSwapChain.QueryInterface<IDXGISwapChain3>();

        var nativePanel = SwapChainPanelNativeHelper.GetNative(DxPanel);
        nativePanel.SetSwapChain(_swapChain.NativePointer);
    }

    private void StartRenderLoop()
    {
        CompositionTarget.Rendering += (_, __) => Render();
    }

    private void Render()
    {
        // Reset
        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, null);

        // Transition to render target
        var barrierToRT = ResourceBarrier.BarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.Present,
            ResourceStates.RenderTarget
        );

        _commandList.ResourceBarrier(barrierToRT);

        // Get RTV handle
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle = rtvHandle.Offset((int)_frameIndex, _rtvDescriptorSize);

        // Clear background (e.g., dark blue)
        var clearColor = new Color4(0.1f, 0.2f, 0.4f, 1.0f);
        _commandList.ClearRenderTargetView(rtvHandle, clearColor);

        // Clear a smaller rectangle with another color to simulate a rectangle
        var rect = new Vortice.RawRect(100, 100, 400, 300);

        var rectColor = new Color4(0.9f, 0.3f, 0.1f, 1.0f);
        _commandList.ClearRenderTargetView(rtvHandle, rectColor, [rect]);

        // Transition: RenderTarget → Present
        var barrierToPresent = ResourceBarrier.BarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.RenderTarget,
            ResourceStates.Present
        );

        _commandList.ResourceBarrier(barrierToPresent);

        _commandList.Close();

        // Execute
        _commandQueue.ExecuteCommandList(_commandList);

        // Present
        _swapChain.Present(1, PresentFlags.None);

        MoveToNextFrame();
    }

    private void MoveToNextFrame()
    {
        var currentFence = _fenceValue;
        _commandQueue.Signal(_fence, currentFence);
        _fenceValue++;

        if (_fence.CompletedValue < currentFence)
        {
            _fence.SetEventOnCompletion(currentFence, _fenceEvent);
            WaitForSingleObject(_fenceEvent, unchecked((int)0xFFFFFFFF));
        }

        _frameIndex = _swapChain.CurrentBackBufferIndex;
    }

    private void DxPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeSwapChain((uint)e.NewSize.Width, (uint)e.NewSize.Height);
    }

    private void ResizeSwapChain(uint width, uint height)
    {
        if (_swapChain == null || width == 0 || height == 0)
            return;

        // Wait for GPU to finish
        MoveToNextFrame();

        // Release old backbuffers
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i]?.Dispose();
            _renderTargets[i] = null;
        }

        // Resize buffers
        _swapChain.ResizeBuffers(
            FrameCount,
            width,
            height,
            Format.R8G8B8A8_UNorm,
            SwapChainFlags.None);

        _frameIndex = _swapChain.CurrentBackBufferIndex;

        // Recreate RTVs
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

        for (uint i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
            rtvHandle = rtvHandle.Offset(1, _rtvDescriptorSize);
        }
    }


    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventEx(
        IntPtr lpEventAttributes,
        string lpName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        IntPtr hHandle,
        int dwMilliseconds);
}
