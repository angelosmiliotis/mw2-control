using System.Windows;
using System.Runtime.InteropServices;

namespace MW2Control.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.StartMonitoring();
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Closed += async (_, _) =>
        {
            await Task.WhenAll(_viewModel.DisposeAsync(), NetworkView.DisposeAsync());
        };
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var enabled = 1;
            _ = DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                20, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int attributeValue, int attributeSize);
}
