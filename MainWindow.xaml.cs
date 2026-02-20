using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Direct3D12Demo;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private D3dPipeline _pipeline;
    private uint _frameCount = 0;

    public MainWindow()
    {
        InitializeComponent();

        _pipeline = new D3dPipeline();

        Activated += MainWindow_Activated;
        DxPanel.SizeChanged += DxPanel_SizeChanged;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= MainWindow_Activated;

        _pipeline.InitializeD3D(DxPanel);

        StartRenderLoop();
    }

    private void StartRenderLoop()
    {
        CompositionTarget.Rendering += (_, __) =>
        {
            try
            {
                _pipeline.Render();

                _frameCount++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_frameCount} {ex}");
                throw; // or comment this out to keep the app running
            }
        };
    }

    private void DxPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pipeline.ResizeSwapChain(Convert.ToUInt32(e.NewSize.Width), Convert.ToUInt32(e.NewSize.Height));
    }
}
