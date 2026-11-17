using System.Windows;
using System.Windows.Automation;
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
    private readonly ReposPage _repos = new();
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
        if (_dragging)
            return;

        _model.Update(App.Current);
        _market.Update(App.Current);
        _repos.Update(App.Current);

        if (Page.Content is MarketDetailPage detail)
            detail.Item.Refresh();

        if (Page.Content is not PluginPage { IsCurrent: false } page)
            return;

        var typingIn = (Keyboard.FocusedElement as TextBox)?.DataContext is SettingItem item ? item.Label : null;
        Page.Content = new PluginPage(page.Plugin);

        if (typingIn is not null)
            Dispatcher.InvokeAsync(() =>
            {
                if (Find<TextBox>(Page, box => box.DataContext is SettingItem setting && setting.Label == typingIn) is not { } box)
                    return;

                box.Focus();
                box.CaretIndex = box.Text.Length;
            }, DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowAttribute(handle, Native.DwmUseImmersiveDarkMode, 1);
        SetWindowAttribute(handle, Native.DwmBorderColor, 0x00262222);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? MaximizedInset() : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private Thickness MaximizedInset()
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var dpi = (uint)Math.Round(96 * scale.DpiScaleX);
        var padding = Native.GetSystemMetricsForDpi(Native.SmCxPaddedBorder, dpi);
        var x = (Native.GetSystemMetricsForDpi(Native.SmCxFrame, dpi) + padding) / scale.DpiScaleX;
        var y = (Native.GetSystemMetricsForDpi(Native.SmCyFrame, dpi) + padding) / scale.DpiScaleY;

        return new Thickness(x, y, x, y);
    }

    private static void SetWindowAttribute(IntPtr handle, int attribute, int value) =>
        Native.DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

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

    private void MarketTab_Click(object sender, RoutedEventArgs e)
    {
        Navigate(_market);
        _ = App.Current.RefreshCatalogAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Navigate(Page.Content is MarketDetailPage or ReposPage ? _market : _model);

    private void Repos_Click(object sender, RoutedEventArgs e) => Navigate(_repos);

    private async void Install_Click(object sender, RoutedEventArgs e) => await ItemOf<CatalogItem>(sender).InstallAsync();

    private void AcceptRepos_Click(object sender, RoutedEventArgs e) => _repos.Accept();

    private async void AddRepo_Click(object sender, RoutedEventArgs e) => await _repos.AddAsync();

    private async void RepoUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await _repos.AddAsync();
    }

    private async void RemoveRepo_Click(object sender, RoutedEventArgs e) => await _repos.RemoveAsync(ItemOf<RepoRow>(sender));

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is PluginPage page)
            page.Confirming = true;
    }

    private void UninstallCancel_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is PluginPage page)
            page.Confirming = false;
    }

    private async void UninstallConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is PluginPage page && await page.RunAsync(() => App.Current.UninstallAsync(page.Plugin.Loaded)))
            Navigate(_model);
    }

    private async void PluginUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Content is PluginPage { Plugin.AvailableUpdate: { } update } page && await page.RunAsync(() => App.Current.InstallAsync(update)))
            Navigate(_model);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Navigate(new PluginPage(ItemOf<PluginItem>(sender)));

    private void Catalog_Click(object sender, RoutedEventArgs e) => Navigate(new MarketDetailPage(ItemOf<CatalogItem>(sender)));

    private void Chip_Checked(object sender, RoutedEventArgs e) => _market.Category = ItemOf<CategoryOption>(sender).Label;

    private void Logs_Click(object sender, RoutedEventArgs e) => App.OpenFolder(AppPaths.Logs);

    private void Link_Click(object sender, RoutedEventArgs e) => App.OpenFolder(ItemOf<LinkItem>(sender).Url.AbsoluteUri);

    private void Text_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TextItem item)
            item.Reveal(true);
    }

    private void Text_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TextItem item)
            Dispatcher.InvokeAsync(() => item.Reveal(false));
    }

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
        Dispatcher.InvokeAsync(() => Find<Button>(Page, grip => grip.Name == "Grip" && grip.DataContext == item)?.Focus(),
            DispatcherPriority.Loaded);
    }

    private void CommitOrder() => App.Current.Host.SetOrder(_model.Sources.Select(item => item.Id));

    private static T? Find<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T candidate && match(candidate))
                return candidate;

            if (Find(child, match) is { } found)
                return found;
        }

        return null;
    }
}
