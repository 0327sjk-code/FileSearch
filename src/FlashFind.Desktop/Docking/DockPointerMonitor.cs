// 文件用途：浮栏展开期间的鼠标边界兜底；收起、隐藏和关闭时停止，不注册全局钩子。
using System.Windows.Threading;

namespace FlashFind.Desktop.Docking;

internal sealed class DockPointerMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _check;
    private bool _disposed;
    public bool IsRunning => _timer.IsEnabled;

    public DockPointerMonitor(Dispatcher dispatcher, Action check)
    {
        _check = check;
        _timer = new(DispatcherPriority.Input, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(DockMetrics.PointerCheckMilliseconds) };
        _timer.Tick += Tick;
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        if (enabled && !_timer.IsEnabled) _timer.Start();
        else if (!enabled) _timer.Stop();
    }

    private void Tick(object? sender, EventArgs args) => _check();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick;
    }
}
