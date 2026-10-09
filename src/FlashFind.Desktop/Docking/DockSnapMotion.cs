// 文件用途：松手后的短促吸附动画；计时器仅在160毫秒动画期间工作。
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace FlashFind.Desktop.Docking;

internal sealed class DockSnapMotion : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render)
    { Interval = TimeSpan.FromSeconds(1 / DockMetrics.FramesPerSecond) };
    private readonly Stopwatch _time = new();
    private Rect _from, _to;
    private Action<Rect>? _frame;
    private Action? _complete;
    public DockSnapMotion() => _timer.Tick += Tick;
    public void Start(Rect from, Rect to, Action<Rect> frame, Action complete, bool animate)
    {
        _timer.Stop(); _from = from; _to = to; _frame = frame; _complete = complete;
        if (!animate) { Finish(); return; }
        _time.Restart(); _timer.Start();
    }
    private void Tick(object? sender, EventArgs args)
    {
        double progress = Math.Min(_time.Elapsed.TotalMilliseconds / DockMetrics.SnapMilliseconds, 1);
        _frame?.Invoke(DockGeometry.Interpolate(_from, _to, 1 - Math.Pow(1 - progress, 5)));
        if (progress >= 1) Finish();
    }
    private void Finish()
    {
        _timer.Stop(); _time.Stop(); _frame?.Invoke(_to);
        Action? complete = _complete; _frame = null; _complete = null; complete?.Invoke();
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; _frame = null; _complete = null; }
}
