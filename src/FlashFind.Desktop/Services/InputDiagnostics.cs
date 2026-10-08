// 文件用途：在未显示的 WPF 窗口内验证输入提示状态，不调用键盘焦点 API。
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

internal static class InputDiagnostics
{
    public static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(MainWindow window)
    {
        var input = (TextBox)window.FindName("SearchInput");
        var placeholder = (TextBlock)window.FindName("SearchPlaceholder");
        // 只在离屏诊断实例中改变依赖属性，避免真正移动用户的键盘焦点。
        var key = (DependencyPropertyKey?)typeof(UIElement).GetField("IsKeyboardFocusWithinPropertyKey",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("当前 WPF 运行时不支持离屏焦点状态诊断。");
        string previousText = input.Text;
        bool previousFocus = input.IsKeyboardFocusWithin;
        var results = new Dictionary<string, bool>();
        try
        {
            await VerifyStateAsync("EmptyAndUnfocusedShowsPlaceholder", "", false, Visibility.Visible);
            await VerifyStateAsync("UncommittedInputFocusHidesPlaceholder", "", true, Visibility.Collapsed);
            await VerifyStateAsync("TypedTextHidesPlaceholder", "中文文件", true, Visibility.Collapsed);
            await VerifyStateAsync("TypedTextAfterBlurHidesPlaceholder", "中文文件", false, Visibility.Collapsed);
            await VerifyStateAsync("ClearedWhileFocusedHidesPlaceholder", "", true, Visibility.Collapsed);
            await VerifyStateAsync("EmptyAfterBlurShowsPlaceholder", "", false, Visibility.Visible);
            return results;
        }
        finally
        {
            input.Text = previousText;
            input.SetValue(key, previousFocus);
            await FlushAsync();
        }

        async Task VerifyStateAsync(string name, string text, bool focused, Visibility expected)
        {
            input.Text = text;
            input.SetValue(key, focused);
            await FlushAsync();
            bool passed = placeholder.Visibility == expected;
            results.Add(name, passed);
            if (!passed) throw new InvalidOperationException($"输入提示自检失败：{name}，实际为 {placeholder.Visibility}。");
        }

        static async Task FlushAsync() => await Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, DispatcherPriority.ApplicationIdle);
    }
}
