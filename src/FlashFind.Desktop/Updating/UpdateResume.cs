// 文件用途：更新重启时恢复同一查询、分类和临时硬盘筛选，永久设置仍由原设置文件管理。
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Updating;

public static class UpdateResume
{
    public static UpdateResumeState Capture(MainViewModel model, bool inTray) =>
        new(inTray, model.Query, model.Category.Category.ToString(), model.Drive.Root);

    public static void Apply(MainViewModel model, UpdateResumeState state)
    {
        model.Category = model.Categories.FirstOrDefault(item => item.Category.ToString() == state.Category) ?? model.Categories[0];
        model.Drive = model.DriveFilters.FirstOrDefault(item => string.Equals(item.Root, state.DriveRoot, StringComparison.OrdinalIgnoreCase)) ?? model.DriveFilters[0];
        model.Query = state.Query;
    }
}
