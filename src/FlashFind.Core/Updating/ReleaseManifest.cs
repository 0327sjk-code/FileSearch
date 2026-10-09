// 文件用途：校验 GitHub 发布清单，绑定不可变版本资产，防止下载跨版本。
namespace FlashFind.Core.Updating;

public sealed record ReleaseManifest(string Version, string ExecutableUrl, string Sha256, long Size)
{
    public System.Version Validate()
    {
        System.Version version = UpdateProduct.ParseVersion(Version);
        if (Size <= 0 || Size > UpdateProduct.MaximumExecutableBytes || string.IsNullOrEmpty(Sha256) || Sha256.Length != 64 ||
            Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("更新文件大小或校验码无效。");
        if (!Uri.TryCreate(ExecutableUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.Host != "github.com" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            Uri.UnescapeDataString(uri.AbsolutePath) != Uri.UnescapeDataString(UpdateProduct.ExecutableUri(Version).AbsolutePath))
            throw new InvalidDataException("更新文件地址与发布版本不一致。");
        return version;
    }
}
