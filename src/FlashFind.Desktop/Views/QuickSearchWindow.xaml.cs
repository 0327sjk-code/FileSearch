// 文件用途：悬浮搜索状态、即时回缩、固定输入栏动画、真实查询和键盘定位。
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Docking;
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Views;

public partial class QuickSearchWindow : Window
{
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(QuickSearchWindow), new PropertyMetadata(0d, VisualChanged));
    public static readonly DependencyProperty PanelHeightProperty = DependencyProperty.Register(nameof(PanelHeight), typeof(double), typeof(QuickSearchWindow), new PropertyMetadata(DockMetrics.Thumb, VisualChanged));
    private readonly IDockEnvironment _environment;
    private readonly DockSnapMotion _snap = new();
    private readonly DockPointerMonitor _pointerMonitor;
    private readonly DockDragController _dragController;
    private DockLayout _layout;
    private HwndSource? _source;
    private bool _placing, _dragging;
    public bool IsExpanded { get; private set; }
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    public double PanelHeight { get => (double)GetValue(PanelHeightProperty); set => SetValue(PanelHeightProperty, value); }
    public DockLayout Layout => _layout;
    public DockSettings Placement => _layout.Placement;
    public MainViewModel ViewModel { get; }
    public event Action? RestoreRequested;
    public event Action<DockSettings>? PlacementChanged;

    public QuickSearchWindow(MainViewModel model, IDockEnvironment? environment = null)
    {
        ViewModel = model; _environment = environment ?? new NativeDockEnvironment();
        _pointerMonitor = new(Dispatcher, () => PointerLeft(_environment.Cursor));
        _layout = DockGeometry.Calculate((model.Settings.Dock ?? new()).Normalize(), _environment.Resolve(model.Settings.Dock?.Monitor ?? ""), 1);
        InitializeComponent(); DataContext = model; ApplyLayout(_layout);
        SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); _source.AddHook(Messages); PlaceNative(Placement); };
        model.PropertyChanged += ModelChanged;
        _dragController = new DockDragController(this, new UIElement[] { PeekHandle, DragHandle }, _environment, () => _layout.Scale,
            StartDrag, MoveDrag, SnapTo, HandleClick, QueuePointerCheck);
        ConfigurePointerHandling();
        Closed += (_, _) => { StopPointerHandling(); model.PropertyChanged -= ModelChanged; _source?.RemoveHook(Messages); _snap.Dispose(); };
    }
    public void ApplyLayout(DockLayout layout)
    {
        _layout = layout; Width = RenderRoot.Width = layout.LogicalSize.Width; Height = RenderRoot.Height = layout.LogicalSize.Height;
        Surface.VerticalAlignment = layout.Above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Surface.CornerRadius = DockOutline.Corners(Placement.Edge);
        FirstRow.Height = layout.Above ? new(1, GridUnitType.Star) : new(DockMetrics.Thumb);
        SecondRow.Height = layout.Above ? new(DockMetrics.Thumb) : new(1, GridUnitType.Star);
        Grid.SetRow(SearchBar, layout.Above ? 1 : 0); Grid.SetRow(Details, layout.Above ? 0 : 1);
        SearchBar.Margin = Placement.Edge == "left" ? new(26, 0, 12, 0) : new(12, 0, Placement.Edge == "right" ? 26 : 12, 0);
        ((RotateTransform)CollapseArrow.RenderTransform).Angle = Placement.Edge switch { "left" => 180, "top" => -90, "bottom" => 90, _ => 0 };
        Rect peek = layout.LocalPeek; Canvas.SetLeft(PeekHandle, peek.X); Canvas.SetTop(PeekHandle, peek.Y);
        PeekHandle.Width = peek.Width; PeekHandle.Height = peek.Height;
        BeginAnimation(PanelHeightProperty, null); PanelHeight = DesiredHeight(); UpdateVisual();
    }
    public void PlaceNative(DockSettings dock)
    {
        if (_placing || _source is null) return;
        _placing = true;
        try
        {
            DockScreen screen = _environment.Resolve(dock.Monitor);
            double scale = DockNative.MoveToMonitor(_source.Handle, screen);
            DockLayout layout = DockGeometry.Calculate(dock, screen, scale); ApplyLayout(layout); DockNative.Place(_source.Handle, layout.Window);
        }
        finally { _placing = false; }
    }
    public void Expand(bool focus = false, bool animate = true)
    {
        if (_dragging) return;
        bool wasExpanded = IsExpanded;
        // 从托盘或键盘主动打开时，允许先输入；鼠标首次进入浮栏后再按离开行为收起。
        _awaitingPointerEntry = focus && !IsInsidePanel(_environment.Cursor);
        IsExpanded = true; SurfaceContents.IsHitTestVisible = true; UpdateResults();
        Animate(RevealProperty, Reveal, 1, DockMetrics.OpenMilliseconds, animate, true);
        UpdatePointerMonitor();
        if (!wasExpanded) TraceDockState("expand");
        if (focus && IsVisible) { Activate(); QuickInput.Focus(); }
    }
    public void Collapse(bool animate = true, string reason = "manual")
    {
        _pointerMonitor.SetEnabled(false);
        if (!IsExpanded && Reveal == 0) return;
        TraceDockState("collapse/" + reason);
        _composing = _awaitingPointerEntry = false;
        IsExpanded = false; _suppressHover = true; SurfaceContents.IsHitTestVisible = false;
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
        Animate(RevealProperty, Reveal, 0, DockMetrics.CloseMilliseconds, animate, false, () => { if (!IsExpanded) DetachResults(); });
    }
    private double DesiredHeight()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Query)) return DockMetrics.Thumb;
        double rows = ViewModel.Results.Count == 0 ? DockMetrics.EmptyHeight : Math.Min(ViewModel.Results.Count, 5) * DockMetrics.RowHeight;
        return Math.Min(_layout.LogicalSize.Height, DockMetrics.Thumb + DockMetrics.ToolbarHeight + DockMetrics.FooterHeight + rows);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MainViewModel.Query) or nameof(MainViewModel.Results))) return;
        if (IsExpanded) UpdateResults();
        Animate(PanelHeightProperty, PanelHeight, DesiredHeight(), DockMetrics.ResizeMilliseconds, IsExpanded, true);
    }
    private void UpdateResults()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(ViewModel.Query);
        Details.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        if (!hasQuery) { DetachResults(); return; }
        QuickResults.ItemsSource = ViewModel.Results;
        if (!BindingOperations.IsDataBound(QuickResults, System.Windows.Controls.Primitives.Selector.SelectedItemProperty))
            BindingOperations.SetBinding(QuickResults, System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.Selected)) { Mode = BindingMode.TwoWay });
    }
    private void DetachResults()
    {
        BindingOperations.ClearBinding(QuickResults, System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
        QuickResults.ItemsSource = null;
    }
    private static void VisualChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    { var window = (QuickSearchWindow)owner; if (window.IsInitialized) window.UpdateVisual(); }
    private void UpdateVisual()
    {
        if (_dragging) return;
        Surface.Height = PanelHeight;
        Rect clip = DockGeometry.Interpolate(_layout.LocalPeek, _layout.Panel(PanelHeight), Reveal);
        RenderRoot.Clip = DockOutline.Clip(clip, Placement.Edge);
        SurfaceContents.Opacity = Reveal; PeekSearch.Opacity = 1 - Reveal; PeekGrip.Opacity = Reveal;
    }
    private void Animate(DependencyProperty property, double from, double to, double milliseconds, bool requested, bool exponential, Action? completed = null)
    {
        BeginAnimation(property, null); SetValue(property, to);
        if (!requested || !SystemParameters.ClientAreaAnimation || Math.Abs(from - to) < .001) { completed?.Invoke(); return; }
        var motion = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = exponential ? new ExponentialEase { Exponent = 7, EasingMode = EasingMode.EaseOut } : new QuarticEase { EasingMode = EasingMode.EaseOut } };
        motion.Completed += (_, _) => completed?.Invoke(); BeginAnimation(property, motion, HandoffBehavior.SnapshotAndReplace);
    }
    private void QuickKeyDown(object sender, KeyEventArgs args)
    {
        if (_composing || args.Key == Key.ImeProcessed) return;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Escape) { Collapse(reason: "escape"); args.Handled = true; }
        else if (key is Key.Enter or Key.Return) { ViewModel.LocateCommand.Execute(null); args.Handled = true; }
        else if (key is Key.Up or Key.Down && ViewModel.Results.Count > 0)
        {
            int next = Math.Clamp(QuickResults.SelectedIndex + (key == Key.Down ? 1 : -1), 0, ViewModel.Results.Count - 1);
            QuickResults.SelectedIndex = next; QuickResults.ScrollIntoView(QuickResults.SelectedItem); args.Handled = true;
        }
        else if (key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { QuickInput.Focus(); QuickInput.SelectAll(); args.Handled = true; }
    }
    private void ResultDoubleClick(object sender, MouseButtonEventArgs args)
    {
        DependencyObject? item = args.OriginalSource as DependencyObject;
        while (item is not null && item is not ListBoxItem) { if (item is Button) return; item = VisualTreeHelper.GetParent(item); }
        if (item is ListBoxItem row && row.DataContext is FileResult file) ViewModel.OpenCommand.Execute(file);
    }
    private void RestoreClick(object sender, RoutedEventArgs args) => RestoreRequested?.Invoke();
    private void CollapseClick(object sender, RoutedEventArgs args) => Collapse();
    private void StartDrag()
    {
        Collapse(false, "drag-start"); _dragging = true; Surface.Visibility = PeekCanvas.Visibility = Visibility.Collapsed;
        DragGhost.Visibility = Visibility.Visible; RenderRoot.Clip = null;
    }
    private void MoveDrag(Point pointer)
    {
        double scale = CurrentScale; Width = RenderRoot.Width = DockMetrics.DragWidth; Height = RenderRoot.Height = DockMetrics.Thumb;
        DragGhost.Width = DockMetrics.DragWidth; DragGhost.Height = DockMetrics.Thumb;
        if (_source is not null) DockNative.Place(_source.Handle, new(pointer.X - DockMetrics.DragWidth * scale / 2, pointer.Y - DockMetrics.Thumb * scale / 2, DockMetrics.DragWidth * scale, DockMetrics.Thumb * scale));
    }
    private double CurrentScale => _source is null ? _layout.Scale : Math.Max(DockNative.GetDpiForWindow(_source.Handle), 96) / 96d;
    public void SnapTo(Point pointer)
    {
        double scale = CurrentScale; DockScreen screen = _environment.At(pointer);
        DockSettings dock = DockGeometry.Nearest(pointer, screen, scale, Placement.Enabled);
        DockLayout target = DockGeometry.Calculate(dock, screen, scale); _suppressHover = true;
        if (_source is not null && _dragging)
        {
            DockNative.GetWindowRect(_source.Handle, out DockNative.NativeRect current);
            _snap.Start(current.ToRect(), target.Peek, rectangle =>
            {
                Width = RenderRoot.Width = DragGhost.Width = rectangle.Width / scale;
                Height = RenderRoot.Height = DragGhost.Height = rectangle.Height / scale;
                DockNative.Place(_source.Handle, rectangle);
            }, Finish, IsVisible && SystemParameters.ClientAreaAnimation);
        }
        else Finish();
        PlacementChanged?.Invoke(dock);
        void Finish()
        {
            _dragging = false; DragGhost.Visibility = Visibility.Collapsed; Surface.Visibility = PeekCanvas.Visibility = Visibility.Visible;
            if (_source is not null) PlaceNative(dock); else ApplyLayout(target);
            QueuePointerCheck();
        }
    }
    private IntPtr Messages(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (!_placing && !_dragging && message is DockNative.DpiChanged or DockNative.DisplayChanged or DockNative.SettingChanged)
            Dispatcher.BeginInvoke(() => { if (!_placing && !_dragging && IsVisible) PlaceNative(Placement); });
        return IntPtr.Zero;
    }
}
