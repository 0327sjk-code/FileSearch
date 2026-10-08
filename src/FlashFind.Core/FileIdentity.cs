// 文件用途：不拼接完整路径的文件身份和大小写无关比较。
namespace FlashFind.Core;

public readonly record struct FileIdentity(string ParentPath, string Name)
{
    public static IEqualityComparer<FileIdentity> Comparer { get; } = new IdentityComparer();
    public static FileIdentity FromPath(string path) => new(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path,
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

    private sealed class IdentityComparer : IEqualityComparer<FileIdentity>
    {
        public bool Equals(FileIdentity left, FileIdentity right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.ParentPath, right.ParentPath) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name);
        public int GetHashCode(FileIdentity value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.ParentPath),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}
