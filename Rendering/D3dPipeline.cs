using System;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using System.Numerics;
using Vortice.Direct3D12.Debug;
using Vortice.Mathematics;
using MUXC = Microsoft.UI.Xaml.Controls;

namespace Direct3D12Demo;

public sealed class D3dPipeline : IDisposable
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
    private readonly ulong[] _frameFenceValues = new ulong[FrameCount];

    private uint _rtvDescriptorSize;
    private uint _frameIndex;

    // GPU pipeline
    private ID3D12RootSignature _rootSignature;
    private ID3D12PipelineState _pipelineState;

    // Geometry
    private ID3D12Resource _vertexBuffer;
    private VertexBufferView _vertexBufferView;

    // Constant buffer
    private ID3D12DescriptorHeap _cbvHeap;
    private ID3D12Resource _constantBuffer;
    private IntPtr _constantBufferPtr;

    // Animation
    private float _angleDegrees = 0f;

    // Vertex struct
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex(float x, float y, Vector4 color)
    {
        public Vector2 Position = new Vector2(x, y);
        public Vector4 Color = color;
    }

    public void InitializeD3D(MUXC.SwapChainPanel swapChainPanel)
    {
        EnableDebuggingIfSupported();

        CreateDevice();
        CreateCommandQueue();
        CreateSwapChainForPanel(swapChainPanel);
        CreateRenderTargets();
        CreateCommandList();
        CreateFence();
        CreatePipelineAndResources();
    }

    public void Render()
    {
        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, _pipelineState);

        // Transition to render target
        var toRenderTarget = ResourceBarrier.BarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.Present,
            ResourceStates.RenderTarget);

        _commandList.ResourceBarrier(toRenderTarget);

        // RTV handle
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle = rtvHandle.Offset((int)_frameIndex, _rtvDescriptorSize);

        // Clear background
        var clearColor = new Color4(0.1f, 0.2f, 0.4f, 1.0f);
        _commandList.ClearRenderTargetView(rtvHandle, clearColor);

        // Viewport + scissor
        _commandList.RSSetViewport(new Viewport(
            0, 0,
            _swapChain.Description1.Width,
            _swapChain.Description1.Height));

        _commandList.RSSetScissorRect(new Vortice.RawRect(
            0, 0,
            (int)_swapChain.Description1.Width,
            (int)_swapChain.Description1.Height));

        // Set render target
        _commandList.OMSetRenderTargets(rtvHandle);

        // Root signature + CBV heap
        _commandList.SetGraphicsRootSignature(_rootSignature);
        _commandList.SetDescriptorHeaps([_cbvHeap]);
        _commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress);

        //
        // Update rotation matrix
        //
        _angleDegrees += 1f;
        if (_angleDegrees >= 360f) _angleDegrees -= 360f;

        float rad = (float)(_angleDegrees * Math.PI / 180.0);
        Matrix4x4 world = Matrix4x4.CreateRotationZ(rad);

        Marshal.StructureToPtr(world, _constantBufferPtr, false);

        // IA setup
        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _commandList.IASetVertexBuffers(0, _vertexBufferView);

        // Draw 6 vertices (2 triangles)
        _commandList.DrawInstanced(6, 1, 0, 0);

        // Transition back to present
        var toPresent = ResourceBarrier.BarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.RenderTarget,
            ResourceStates.Present);

        _commandList.ResourceBarrier(toPresent);

        _commandList.Close();

        _commandQueue.ExecuteCommandList(_commandList);
        _swapChain.Present(1, PresentFlags.None);

        MoveToNextFrame();
    }

    public void ResizeSwapChain(uint width, uint height)
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

    private static void EnableDebuggingIfSupported()
    {
#if DEBUG
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug debug).Success)
        {
            debug.EnableDebugLayer();

            // Optional: GPU-based validation
            if (debug.QueryInterface<ID3D12Debug1>() is ID3D12Debug1 debug1)
            {
                debug1.SetEnableGPUBasedValidation(true);
            }
        }
#endif
    }

    private void CreateSwapChainForPanel(MUXC.SwapChainPanel dxPanel)
    {
        // Get panel size
        uint width = (uint)Math.Max(1, dxPanel.ActualWidth);
        uint height = (uint)Math.Max(1, dxPanel.ActualHeight);

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

        var nativePanel = SwapChainPanelNativeHelper.GetNative(dxPanel);
        nativePanel.SetSwapChain(_swapChain.NativePointer);
    }

    // Private helpers: paste bodies from MainWindow

    private void CreateDevice()
    {
        // Create DXGI factory
        _dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);

        // Create device
        _device = D3D12.D3D12CreateDevice<ID3D12Device>(null, FeatureLevel.Level_11_0);
    }

    private void CreateCommandQueue()
    {
        // Create command queue
        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        _commandQueue = _device.CreateCommandQueue(queueDesc);
    }

    private void CreateRenderTargets()
    {
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
    }

    private void CreateCommandList()
    {
        // Command allocator & list
        _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            nodeMask: 0,
            type: CommandListType.Direct,
            commandAllocator: _commandAllocator,
            initialState: null);

        // initial close. We won't record commands until Render(), but the command list needs to be closed before it can be reset.
        _commandList.Close();
    }

    private void CreateFence()
    {
        // Fence
        _fence = _device.CreateFence(0);
        _fenceValue = 0;
        for (int i = 0; i < FrameCount; i++)
            _frameFenceValues[i] = 0;

        _fenceEvent = CreateEventEx(IntPtr.Zero, null, 0, 0);

        _frameIndex = _swapChain.CurrentBackBufferIndex;
    }

    private void WaitForGPU()
    {
        // Paste old WaitForGPU() body here
    }

    public void Dispose()
    {
        // Paste your existing cleanup logic here
        // (whatever you had in MainWindow for D3D objects)
    }

    private void CreatePipelineAndResources()
    {
        //
        // 1. Root signature (one CBV at register b0, space 0)
        //
        // Root parameter: CBV at register b0, space 0
        var rootParam = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0),
            ShaderVisibility.Vertex
        );

        // Root signature description (version 1.1)
        var rootSigDesc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [rootParam],
            Array.Empty<StaticSamplerDescription>()
        );

        // Create the root signature
        _rootSignature = _device.CreateRootSignature(rootSigDesc);

        //
        // 2. HLSL shaders
        //
        string vsSource = @"
cbuffer Transform : register(b0)
{
    float4x4 gWorld;
};

struct VSInput
{
    float2 pos : POSITION;
    float4 col : COLOR;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float4 col : COLOR;
};

PSInput main(VSInput input)
{
    PSInput o;
    o.pos = mul(float4(input.pos, 0.0f, 1.0f), gWorld);
    o.col = input.col;
    return o;
}
";

        string psSource = @"
struct PSInput
{
    float4 pos : SV_POSITION;
    float4 col : COLOR;
};

float4 main(PSInput input) : SV_TARGET
{
    return input.col;
}
";

        using Blob vsBlob = Compiler.Compile(
            vsSource,
            entryPoint: "main",
            sourceName: null,
            macros: null,
            include: null,
            profile: "vs_5_0",
            ShaderFlags.None,
            EffectFlags.None
        );

        using Blob psBlob = Compiler.Compile(
            psSource,
            entryPoint: "main",
            sourceName: null,
            macros: null,
            include: null,
            profile: "ps_5_0",
            ShaderFlags.None,
            EffectFlags.None
        );

        //
        // 3. Input layout
        //
        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float,       0, 0),
            new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 8, 0),
        };

        //
        // 4. Pipeline state
        //
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = vsBlob.AsBytes(),
            PixelShader = psBlob.AsBytes(),
            InputLayout = new InputLayoutDescription(inputElements),
            BlendState = BlendDescription.Opaque,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleDescription = new SampleDescription(1, 0),
        };

        // Older-style RTV setup
        psoDesc.RenderTargetFormats[0] = Format.R8G8B8A8_UNorm;

        _pipelineState = _device.CreateGraphicsPipelineState(psoDesc);

        //
        // 5. Vertex buffer (rectangle in NDC)
        //
        var color = new Vector4(0.9f, 0.3f, 0.1f, 1.0f);

        var vertices = new[]
        {
            new Vertex(-0.3f, -0.2f, color),
            new Vertex( 0.3f, -0.2f, color),
            new Vertex( 0.3f,  0.2f, color),

            new Vertex(-0.3f, -0.2f, color),
            new Vertex( 0.3f,  0.2f, color),
            new Vertex(-0.3f,  0.2f, color),
        };

        uint vbSize = (uint)(Marshal.SizeOf<Vertex>() * vertices.Length);

        _vertexBuffer = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)vbSize),
            ResourceStates.GenericRead);

        IntPtr vbPtr;
        unsafe
        {
            void* pData = null;
            _vertexBuffer.Map(0, null, &pData);
            vbPtr = (IntPtr)pData;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Marshal.StructureToPtr(vertices[i], vbPtr + i * Marshal.SizeOf<Vertex>(), false);
        }

        _vertexBuffer.Unmap(0, null);


        _vertexBufferView = new VertexBufferView
        {
            BufferLocation = _vertexBuffer.GPUVirtualAddress,
            SizeInBytes = vbSize,
            StrideInBytes = (uint)Marshal.SizeOf<Vertex>()
        };

        //
        // 6. Constant buffer + CBV
        //
        uint cbSize = (uint)((Marshal.SizeOf<Matrix4x4>() + 255) & ~255); // 256-byte aligned

        _constantBuffer = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer(cbSize),
            ResourceStates.GenericRead);

        unsafe
        {
            void* pData = null;
            _constantBuffer.Map(0, null, &pData);
            _constantBufferPtr = (IntPtr)pData;
        }

        var cbvHeapDesc = new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            1,
            DescriptorHeapFlags.ShaderVisible);

        _cbvHeap = _device.CreateDescriptorHeap(cbvHeapDesc);

        var cbvDesc = new ConstantBufferViewDescription
        {
            BufferLocation = _constantBuffer.GPUVirtualAddress,
            SizeInBytes = cbSize
        };

        _device.CreateConstantBufferView(cbvDesc, _cbvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    private void MoveToNextFrame()
    {
        // Signal for the frame we just submitted
        _fenceValue++;
        _commandQueue.Signal(_fence, _fenceValue);
        _frameFenceValues[_frameIndex] = _fenceValue;

        // Advance to next back buffer
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        // If the next frame is not ready, wait for it
        if (_fence.CompletedValue < _frameFenceValues[_frameIndex])
        {
            _fence.SetEventOnCompletion(_frameFenceValues[_frameIndex], _fenceEvent);
            WaitForSingleObject(_fenceEvent, unchecked((int)0xFFFFFFFF));
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