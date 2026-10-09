// 文件用途：窗口键盘操作、双击、上下文菜单和响应式列宽。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        ResultsGrid.SizeChanged += (_, _) => FitColumns();
        Loaded += (_, _) => SearchInput.Focus();
    }
    public MainViewModel ViewModel => (MainViewModel)DataContext;
    public void FocusSearch() { SearchInput.Focus(); SearchInput.SelectAll(); }
    public void SetResultsActive(bool active)
    {
        if (active)
        {
            BindingOperations.SetBinding(ResultsGrid, ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.Results)));
            BindingOperations.SetBinding(ResultsGrid, System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.Selected)) { Mode = BindingMode.TwoWay });
        }
        else
        {
            BindingOperations.ClearBinding(ResultsGrid, System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
            BindingOperations.ClearBinding(ResultsGrid, ItemsControl.ItemsSourceProperty);
            ResultsGrid.ItemsSource = null;
        }
    }

    private void FitColumns()
    {
        const double CompactTableWidth = 944, MinimumNameWidth = 270;
        ModifiedColumn.Visibility = ResultsGrid.ActualWidth < CompactTableWidth ? Visibility.Collapsed : Visibility.Visible;
        double fixedWidth = ResultsGrid.Columns.Skip(1).Where(x => x.Visibility == Visibility.Visible).Sum(x => x.Width.Value);
        ResultsGrid.Columns[0].Width = new DataGridLength(Math.Max(MinimumNameWidth,
            ResultsGrid.ActualWidth - fixedWidth - SystemParameters.VerticalScrollBarWidth - 2));
    }

    private void WindowKeyDown(object sender, KeyEventArgs args)
    {
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { SearchInput.Focus(); SearchInput.SelectAll(); args.Handled = true; }
        else if (key == Key.Escape)
        {
            if (ViewModel.SettingsOpen) ViewModel.CloseSettingsCommand.Execute(null);
            else ViewModel.ClearCommand.Execute(null);
            args.Handled = true;
        }
        else if (!ViewModel.SettingsOpen && key is Key.Enter or Key.Return)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ViewModel.LocateCommand.Execute(null);
            else ViewModel.OpenCommand.Execute(null);
            args.Handled = true;
        }
        else if (key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !SearchInput.IsKeyboardFocusWithin)
        { ViewModel.CopyCommand.Execute(null); args.Handled = true; }
        else if (key == Key.Down && SearchInput.IsKeyboardFocusWithin && ResultsGrid.Items.Count > 0)
        { ResultsGrid.Focus(); args.Handled = true; }
    }

    private void ResultsDoubleClick(object sender, MouseButtonEventArgs args)
    {
        DependencyObject? source = args.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
        {
            if (source is Button) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (source is DataGridRow row && row.Item is FileResult file)
            ViewModel.OpenCommand.Execute(file);
    }
    private void ContextOpen(object sender, RoutedEventArgs args) => ViewModel.OpenCommand.Execute(null);
    private void ContextLocate(object sender, RoutedEventArgs args) => ViewModel.LocateCommand.Execute(null);
    private void ContextCopy(object sender, RoutedEventArgs args) => ViewModel.CopyCommand.Execute(null);
    private void BackdropMouseDown(object sender, MouseButtonEventArgs args) => ViewModel.CloseSettingsCommand.Execute(null);
}
