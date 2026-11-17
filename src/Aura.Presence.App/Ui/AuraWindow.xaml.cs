using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Aura.Presence.App.Ui;

public partial class AuraWindow : Window
{
    private readonly MainModel _model = new();
    private readonly MarketPage _market = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dragging;

    public AuraWindow()
    {
        InitializeComponent();
        DataContext = _model;
        _clock.Tick += (_, _) => _model.Now?.Tick();
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
        Refresh();
        InstalledTab.IsChecked = true;
        Navigate(_model);
    }

    public void Refresh()
    {
        if (!_dragging)
            _model.Update(App.Current);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowAttribute(handle, Native.DwmUseImmersiveDarkMode, 1);
        SetWindowAttribute(handle, Native.DwmCaptionColor, 0x000E0C0C);
        SetWindowAttribute(handle, Native.DwmTextColor, 0x00EFECEC);
        SetWindowAttribute(handle, Native.DwmBorderColor, 0x00262222);
    }

    private static void SetWindowAttribute(IntPtr handle, int attribute, int value) =>
        Native.DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private void Navigate(object page)
    {
        Page.Content = page;
        Scroller.ScrollToTop();

        if (!SystemParameters.ClientAreaAnimation)
            return;

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 6);
        Page.RenderTransform = shift;
        Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
    }

    private static T ItemOf<T>(object sender) => (T)((FrameworkElement)sender).DataContext;

    private void InstalledTab_Click(object sender, RoutedEventArgs e) => Navigate(_model);

    private void MarketTab_Click(object sender, RoutedEventArgs e) => Navigate(_market);

    private void Back_Click(object sender, RoutedEventArgs e) => Navigate(Page.Content is MarketDetailPage ? _market : _model);

    private void Open_Click(object sender, RoutedEventArgs e) => Navigate(new PluginPage(ItemOf<PluginItem>(sender)));

    private void Catalog_Click(object sender, RoutedEventArgs e) => Navigate(new MarketDetailPage(ItemOf<CatalogItem>(sender)));

    private void Chip_Checked(object sender, RoutedEventArgs e) => _market.Category = ItemOf<CategoryOption>(sender).Label;

    private void Logs_Click(object sender, RoutedEventArgs e) => App.OpenFolder(AppPaths.Logs);

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Now is { } now)
            App.Current.TogglePin(now.SourceId);
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf<PluginItem>(sender);
        await App.Current.Host.SetEnabledAsync(item.Loaded, item.IsOn);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is PluginPage page)
            App.OpenFolder(page.Plugin.Loaded.Context.DataDirectory);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is not PluginPage page)
            return;

        page.Plugin.Loaded.Context.Settings.Clear();
        await App.Current.Host.RestartAsync(page.Plugin.Loaded);
        Page.Content = new PluginPage(page.Plugin);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => _market.Columns = e.NewSize.Width < 700 ? 2 : 3;

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemOf<PluginItem>(sender);
        e.Handled = true;
        _dragging = true;
        item.IsDragging = true;

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
            _dragging = false;
        }

        CommitOrder();
    }

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        if (e.Data.GetData(typeof(PluginItem)) is not PluginItem dragged || ((FrameworkElement)sender).DataContext is not PluginItem target)
            return;

        var from = _model.Sources.IndexOf(dragged);
        var to = _model.Sources.IndexOf(target);

        if (from < 0 || to < 0)
            return;

        e.Effects = DragDropEffects.Move;

        if (from != to)
            _model.Sources.Move(from, to);
    }

    private void Grip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down))
            return;

        var item = ItemOf<PluginItem>(sender);
        var from = _model.Sources.IndexOf(item);
        var to = from + (e.Key == Key.Up ? -1 : 1);
        e.Handled = true;

        if (from < 0 || to < 0 || to >= _model.Sources.Count)
            return;

        _model.Sources.Move(from, to);
        CommitOrder();
        Dispatcher.InvokeAsync(() => FindGrip(Page, item)?.Focus(), DispatcherPriority.Loaded);
    }

    private void CommitOrder() => App.Current.Host.SetOrder(_model.Sources.Select(item => item.Id));

    private static Button? FindGrip(DependencyObject parent, PluginItem item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is Button { Name: "Grip" } grip && grip.DataContext == item)
                return grip;

            if (FindGrip(child, item) is { } found)
                return found;
        }

        return null;
    }
}
