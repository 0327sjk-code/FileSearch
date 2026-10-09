// 文件用途：集中管理发布仓库、固定资产名和稳定版本规则。
namespace FlashFind.Core.Updating;

public static class UpdateProduct
{
    public const string Repository = "0327sjk-code/FileSearch";
    public const string ExecutableName = "文件搜索.exe";
    public const string ProductName = "文件搜索";
    public const string CompanyName = "FlashFind";
    public const long MaximumExecutableBytes = 256L * 1024 * 1024;
    public static Uri ManifestUri { get; } = new($"https://github.com/{Repository}/releases/latest/download/update.json");
    public static Version CurrentVersion => Normalize(typeof(UpdateProduct).Assembly.GetName().Version!);
    public static string DisplayVersion => CurrentVersion.ToString(3);
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidDataException("更新版本格式不正确。");
        string[] parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9')) ||
            !Version.TryParse(text, out Version? result))
            throw new InvalidDataException("更新版本格式不正确。");
        return Normalize(result);
    }

    public static Uri ExecutableUri(string version) => new($"https://github.com/{Repository}/releases/download/v{version}/{Uri.EscapeDataString(ExecutableName)}");
}
