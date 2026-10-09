// 文件用途：可见浮栏的离开判定、输入法保护、捕获结束补查与监测生命周期。
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlashFind.Desktop.Views;

public partial class QuickSearchWindow
{
    private bool _suppressHover, _composing, _awaitingPointerEntry, _closed;
    private DispatcherOperation? _pendingPointerCheck;
    internal bool IsPointerMonitorRunning => _pointerMonitor.IsRunning;

    private void ConfigurePointerHandling()
    {
        MouseEnter += (_, _) =>
        {
            if (!_dragging && !_suppressHover) { _awaitingPointerEntry = false; Expand(); }
        };
        MouseLeave += (_, _) => PointerLeft(_environment.Cursor);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((_, _) => PointerLeft(_environment.Cursor)), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => QueuePointerCheck()), true);
        Deactivated += (_, _) => { _awaitingPointerEntry = false; PointerLeft(_environment.Cursor); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _composing = _awaitingPointerEntry = false;
            UpdatePointerMonitor();
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(QuickInput, (_, _) => SetComposing(true));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(QuickInput, (_, args) =>
            SetComposing(args.TextComposition.CompositionText.Length > 0));
        TextCompositionManager.AddPreviewTextInputHandler(QuickInput, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length == 0) SetComposing(false);
        });
        QuickInput.LostKeyboardFocus += (_, _) => { SetComposing(false); QueuePointerCheck(); };
    }

    public void PointerLeft(Point physicalPointer)
    {
        if (_closed || _placing || _dragging || _dragController.IsTracking) return;
        Point local = ToLocal(physicalPointer);
        bool inside = IsExpanded ? IsInsidePanel(physicalPointer) : _layout.LocalPeek.Contains(local);
        if (inside) { _awaitingPointerEntry = false; return; }
        _suppressHover = false;
        if (IsExpanded && !_composing && !_awaitingPointerEntry)
        {
            Collapse(reason: "pointer-outside");
            _suppressHover = false;
        }
    }

    private Point ToLocal(Point physicalPointer) => new(
        (physicalPointer.X - _layout.Window.X) / _layout.Scale,
        (physicalPointer.Y - _layout.Window.Y) / _layout.Scale);

    private bool IsInsidePanel(Point physicalPointer)
    {
        Point local = ToLocal(physicalPointer);
        // 输入栏锚点不随结果高度变化，保护输入时的布局刷新；不把透明 HWND 余下区域算作浮栏。
        return _layout.Panel(PanelHeight).Contains(local) || _layout.SearchBar.Contains(local);
    }

    private void HandleClick()
    {
        if (IsInsidePanel(_environment.Cursor)) Expand(true);
        QueuePointerCheck();
    }

    private void SetComposing(bool composing)
    {
        if (_composing == composing) return;
        _composing = composing;
        // 等本次输入事件提交文本后再补查，避免 PreviewTextInput 阶段清除焦点而吞掉输入。
        if (!composing) QueuePointerCheck();
    }

    private void QueuePointerCheck()
    {
        if (_closed || _pendingPointerCheck?.Status == DispatcherOperationStatus.Pending) return;
        _pendingPointerCheck = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _pendingPointerCheck = null;
            if (!_closed) PointerLeft(_environment.Cursor);
        }));
    }

    private void UpdatePointerMonitor() =>
        _pointerMonitor.SetEnabled(!_closed && IsExpanded && IsVisible && !_dragging);

    private void TraceDockState(string transition)
    {
        Point pointer = _environment.Cursor;
        ViewModel.RecordDockState($"{transition}; edge={Placement.Edge}; pointer={pointer.X:F0},{pointer.Y:F0}; " +
            $"expanded={IsExpanded}; dragging={_dragging}; capture={_dragController.IsTracking}; composing={_composing}");
    }

    private void StopPointerHandling()
    {
        _closed = true; _pendingPointerCheck?.Abort(); _pendingPointerCheck = null; _pointerMonitor.Dispose();
    }
}
