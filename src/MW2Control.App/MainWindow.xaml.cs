using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MW2Control.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly LobbyNetworkView _networkView;
    private readonly Button[] _presetButtons;
    private static readonly SolidColorBrush SelectedBorder = Brush("#92AE65"), NormalBorder = Brush("#404A35"),
        SelectedText = Brush("#D0E8AD"), NormalText = Brush("#C0C6B5"),
        SelectedSurface = Brush("#2C3823"), NormalSurface = Brush("#162019");
    private bool _ready;

    public MainWindow() : this(new MainViewModel(), false) { }

    internal MainWindow(MainViewModel viewModel, bool offline)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _presetButtons = PresetButtons(DesignRoot).ToArray();
        _networkView = new LobbyNetworkView(offline);
        NetworkPage.Content = _networkView;
        DataContext = _viewModel;
        if (!offline) Loaded += (_, _) => _viewModel.StartMonitoring();
        SourceInitialized += (_, _) => WindowFrame.HideSystemBorder(this);
        Activated += (_, _) => WindowFrame.HideSystemBorder(this);
        Deactivated += (_, _) => WindowFrame.HideSystemBorder(this);
        _viewModel.PropertyChanged += ViewModelChanged;
        Closed += async (_, _) =>
        {
            _viewModel.PropertyChanged -= ViewModelChanged;
            await Task.WhenAll(_viewModel.DisposeAsync(), _networkView.DisposeAsync());
        };
        _ready = true;
        ShowPage("settings");
        RefreshPresets();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MainViewModel.RequestedFov) or nameof(MainViewModel.RequestedFps) or nameof(MainViewModel.UncappedFps))
            RefreshPresets();
    }

    private void RootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) ConfigureLayout(e.NewSize.Width, e.NewSize.Height);
    }

    internal void ConfigureLayout(double width, double height)
    {
        var scale = Math.Clamp(Math.Min(width / 1360, height / 880), .85, 1.7);
        UiScale.ScaleX = UiScale.ScaleY = scale;
        var compact = height / scale < 820;
        HeaderRow.Height = new GridLength(compact ? 64 : 72);
        FooterRow.Height = new GridLength(compact ? 76 : 80);
        SidebarColumn.Width = new GridLength(width / scale < 1250 ? 210 : 230);
        SettingsPage.Margin = compact ? new Thickness(24, 18, 24, 18) : new Thickness(28, 22, 28, 20);
        SettingsHeadingRow.Height = new GridLength(compact ? 72 : 88);
        FirstGap.Height = SecondGap.Height = new GridLength(compact ? 14 : 18);
        SettingsPage.RowDefinitions[1].MinHeight = compact ? 148 : 155;
        SettingsPage.RowDefinitions[3].MinHeight = compact ? 178 : 190;
        SettingsPage.RowDefinitions[5].MinHeight = compact ? 92 : 100;
    }

    internal void ShowPage(string page)
    {
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = page == "network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsNav.IsChecked = page == "settings";
        DiagnosticsNav.IsChecked = page == "diagnostics";
        NetworkNav.IsChecked = page == "network";
        ApplyButton.IsEnabled = page == "settings";
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowPage(sender == DiagnosticsNav ? "diagnostics" : sender == NetworkNav ? "network" : "settings");
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) RefreshPresets();
    }

    private void FovPreset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.InputsEnabled)
            _viewModel.RequestedFov = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    }

    private void FpsPreset(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.InputsEnabled) return;
        _viewModel.UncappedFps = false;
        _viewModel.RequestedFps = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<Button> PresetButtons(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is Button button && button.Tag is string tag && int.TryParse(tag, out _)) yield return button;
            foreach (var nested in PresetButtons(child)) yield return nested;
        }
    }

    private void RefreshPresets()
    {
        foreach (var button in _presetButtons)
        {
            var value = int.Parse((string)button.Tag, CultureInfo.InvariantCulture);
            var selected = Equals(button.CommandParameter, "fov") ? _viewModel.RequestedFov == value : !_viewModel.UncappedFps && _viewModel.RequestedFps == value;
            button.BorderBrush = selected ? SelectedBorder : NormalBorder;
            button.Foreground = selected ? SelectedText : NormalText;
            button.Background = selected ? SelectedSurface : NormalSurface;
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null;
             source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        if (e.ClickCount == 2) MaximizeWindow(sender, e);
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
