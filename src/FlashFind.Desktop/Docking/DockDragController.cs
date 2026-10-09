// 文件用途：只在用户拖动时捕获鼠标；无后台轮询，点击和拖动互不冲突。
using System.Windows;
using System.Windows.Input;

namespace FlashFind.Desktop.Docking;

internal sealed class DockDragController
{
    private readonly UIElement _capture;
    private readonly IDockEnvironment _environment;
    private readonly Func<double> _scale;
    private readonly Action _start, _click, _finished;
    private readonly Action<Point> _move, _end;
    private Point _origin;
    private bool _tracking, _moving;
    public bool IsTracking => _tracking;
    public DockDragController(UIElement capture, IEnumerable<UIElement> handles, IDockEnvironment environment,
        Func<double> scale, Action start, Action<Point> move, Action<Point> end, Action click, Action finished)
    {
        _capture = capture; _environment = environment; _scale = scale; _start = start; _move = move; _end = end; _click = click; _finished = finished;
        foreach (UIElement handle in handles) handle.PreviewMouseLeftButtonDown += Down;
        capture.PreviewMouseMove += Move; capture.PreviewMouseLeftButtonUp += Up;
        capture.LostMouseCapture += (_, _) => Finish(false);
    }
    private void Down(object sender, MouseButtonEventArgs args)
    {
        if (_tracking) return;
        _origin = _environment.Cursor; _moving = false; _tracking = _capture.CaptureMouse(); args.Handled = true;
    }
    private void Move(object sender, MouseEventArgs args)
    {
        if (!_tracking) return;
        Point pointer = _environment.Cursor;
        if (!_moving && (pointer - _origin).Length >= DockMetrics.DragThreshold * _scale()) { _moving = true; _start(); }
        if (_moving) _move(pointer);
        args.Handled = true;
    }
    private void Up(object sender, MouseButtonEventArgs args) { if (!_tracking) return; args.Handled = true; Finish(true); }
    private void Finish(bool clickAllowed)
    {
        if (!_tracking) return;
        bool moved = _moving;
        _tracking = _moving = false; _capture.ReleaseMouseCapture();
        if (moved) _end(_environment.Cursor); else if (clickAllowed) _click();
        _finished();
    }
}
