// 文件用途：更新按钮、状态和进度绑定；保留手动入口且避免并发检查。
using System.Windows.Input;
using FlashFind.Core.Updating;

namespace FlashFind.Desktop.ViewModels;

public sealed class UpdateViewModel : ObservableObject
{
    private string _buttonText = "检查更新", _status = "";
    private bool _busy;
    public UpdateViewModel(Action check) => CheckCommand = new RelayCommand(_ => check(), _ => !Busy);
    public ICommand CheckCommand { get; }
    public string ButtonText { get => _buttonText; private set => Set(ref _buttonText, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length != 0;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Hint => $"当前版本 {UpdateProduct.DisplayVersion} · 每天上午 11:00 自动检查，下载后自动重启";
    public void Checking() { Busy = true; ButtonText = "正在检查…"; Status = "正在检查更新"; }
    public void Downloading(string version, int percent) { ButtonText = $"下载中 {percent}%"; Status = $"正在下载 {version} · {percent}%"; }
    public void Ready(string version) { Busy = true; ButtonText = "准备重启…"; Status = $"{version} 已下载，操作结束后自动重启"; }
    public void Complete(string status) { Busy = false; ButtonText = "检查更新"; Status = status; }
}
