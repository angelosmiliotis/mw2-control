using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MW2Control.App;
using MW2Control.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? "out/screenshots-1.2");
        Directory.CreateDirectory(folder);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MW2Control;component/Styles.xaml") });
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        var exitCode = 0;
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var vm = new MainViewModel(new ControlEngine(new NoGameSource()));
                var window = new MainWindow(vm, offline: true);
                vm.ScanCommand.Execute(null);
                while (vm.Busy) await Task.Delay(10);
                Assert(vm.StateLabel == "GAME NOT RUNNING", "Screenshots use an isolated, disconnected engine");
                VerifyBindings(window, vm);
                foreach (var page in new[] { "settings", "diagnostics", "network" }) Render(window, page, 1360, 880, Path.Combine(folder, page + ".png"));
                foreach (var size in new[] { (1100, 730), (1920, 1080), (2560, 1440), (2560, 1080), (1100, 1200) })
                    Render(window, "settings", size.Item1, size.Item2, Path.Combine(folder, $"settings-{size.Item1}x{size.Item2}.png"));
                VerifyNativeFrame(window);
                Assert(errors.Errors.Count == 0, "No WPF binding errors: " + string.Join("\n", errors.Errors));
                File.WriteAllText(Path.Combine(folder, "checks.txt"), "PASS: real-window bindings, sliders, presets, visual/audio/ping switches, specular menu, command wiring, six responsive sizes, zero native top inset, DWM border suppression. No game, user preferences, packet capture or real firewall access. Screenshots show the application disconnected.");
                window.Close();
                Console.WriteLine("PASS: production UI, native frame and screenshots");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
                Console.Error.WriteLine(error);
                exitCode = 1;
            }
            finally { application.Shutdown(); }
        }));
        application.Run();
        return exitCode;
    }

    private static void VerifyBindings(MainWindow window, MainViewModel vm)
    {
        var root = (FrameworkElement)window.FindName("DesignRoot");
        Arrange(window, root, 1360, 880);
        T Named<T>(string name) => (T)window.FindName(name);
        var fov = Named<Slider>("FovSlider");
        fov.SetCurrentValue(RangeBase.ValueProperty, 97d);
        fov.GetBindingExpression(RangeBase.ValueProperty)!.UpdateSource();
        Assert(vm.RequestedFov == 97, "FOV slider is bound to the live view model");
        fov.SetCurrentValue(RangeBase.ValueProperty, 130d);
        fov.GetBindingExpression(RangeBase.ValueProperty)!.UpdateSource();
        Assert(vm.RequestedFov == 130 && fov.Maximum == 130, "Extended FOV reaches 130 in the real window");
        var fps = Named<Slider>("FpsSlider");
        fps.SetCurrentValue(RangeBase.ValueProperty, 240d);
        fps.GetBindingExpression(RangeBase.ValueProperty)!.UpdateSource();
        Assert(vm.RequestedFps == 240, "FPS slider binding");
        foreach (var pair in new[] { ("Fullbright", vm.Fullbright), ("NoFog", vm.Fog), ("NoGlow", vm.Glow), ("NoDetail", vm.Detail), ("StreamMute", vm.Music), ("NumericPing", vm.Ping) })
        {
            var toggle = Named<CheckBox>(pair.Item1);
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            Assert(pair.Item2.Requested, pair.Item1 + " switch binding");
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        }
        var uncapped = Named<CheckBox>("Uncapped");
        uncapped.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        uncapped.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        Arrange(window, root, 1360, 880);
        Assert(vm.UncappedFps && !fps.IsEnabled, "Uncapped disables FPS slider");
        foreach (var button in LogicalChildren<Button>(root).Where(b => Equals(b.Tag, "333"))) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!vm.UncappedFps && vm.RequestedFps == 333, "FPS preset clears uncapped");
        foreach (var button in LogicalChildren<Button>(root).Where(b => Equals(b.Tag, "90"))) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(vm.RequestedFov == 90, "FOV preset binding");
        var specular = Named<ComboBox>("Specular");
        specular.SetCurrentValue(Selector.SelectedIndexProperty, 3);
        specular.GetBindingExpression(Selector.SelectedIndexProperty)!.UpdateSource();
        Assert(vm.SpecularChoice == 3, "Specular binding");
        vm.SpecularChoice = 0;
        Assert(ReferenceEquals(Named<Button>("ApplyButton").Command, vm.ApplyCommand), "Apply uses the working engine command");
        Assert(LogicalChildren<Button>(root).Any(b => ReferenceEquals(b.Command, vm.ResetCommand)), "Reset uses the working engine command");
        Assert(!vm.ApplyCommand.CanExecute(null), "Disconnected screenshots cannot apply settings");
    }

    private static IEnumerable<T> LogicalChildren<T>(DependencyObject node) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is T target) yield return target;
            foreach (var nested in LogicalChildren<T>(child)) yield return nested;
        }
    }

    private static void Arrange(MainWindow window, FrameworkElement root, int width, int height)
    {
        window.ConfigureLayout(width, height);
        for (var pass = 0; pass < 2; pass++)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private static void Render(MainWindow window, string page, int width, int height, string path)
    {
        window.ShowPage(page);
        var root = (FrameworkElement)window.FindName("DesignRoot");
        Arrange(window, root, width, height);
        var surface = (FrameworkElement)window.FindName("LayoutSurface");
        var bounds = surface.TransformToAncestor(root).TransformBounds(new Rect(surface.RenderSize));
        Assert(Math.Abs(bounds.X) < 1 && Math.Abs(bounds.Y) < 1 && Math.Abs(bounds.Width - width) < 1 && Math.Abs(bounds.Height - height) < 1, "No outer gaps at " + width + "x" + height);
        if (page == "settings")
        {
            foreach (var name in new[] { "FovSlider", "FpsSlider", "Fullbright", "NoFog", "NoGlow", "NoDetail", "Specular", "StreamMute", "NumericPing", "ApplyButton" })
            {
                var control = (FrameworkElement)window.FindName(name);
                var rect = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Assert(rect.X >= -1 && rect.Y >= -1 && rect.Right <= width + 1 && rect.Bottom <= height + 1 && rect.Width > 0 && rect.Height > 0, name + " stays within the window");
            }
        }
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void VerifyNativeFrame(MainWindow window)
    {
        window.Left = window.Top = -20000;
        window.ShowInTaskbar = false;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        Assert(GetWindowRect(handle, out var rect), "Window rectangle available");
        var origin = new NativePoint();
        Assert(ClientToScreen(handle, ref origin) && origin.Y == rect.Top, "Native top inset is zero");
        Assert(!IsWindowVisible(handle), "No window was shown");
        Assert(WindowFrame.HideSystemBorder(window) == 0, "System border suppression accepted");
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class NoGameSource : IProcessSource { public IProcessMemory? OpenTarget() => null; }
    private sealed class BindingErrors : TraceListener
    {
        internal List<string> Errors { get; } = new();
        public override void Write(string? message) { if (message?.Contains("Error:") == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
}
