// 文件用途：目录范围和路径标准化，避免相似前缀误匹配。
namespace FlashFind.Core;

public static class PathRules
{
    public static bool IsWithin(string path, string root)
    {
        string normalized = Path.TrimEndingDirectorySeparator(root);
        if (!path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == normalized.Length || Path.IsPathFullyQualified(normalized) &&
            (Path.EndsInDirectorySeparator(normalized) || path[normalized.Length] is '\\' or '/');
    }

    public static string[] NormalizeRoots(IEnumerable<string> roots) => roots
        .Select(Path.GetFullPath).Select(x => Path.TrimEndingDirectorySeparator(x))
        .Select(x => Path.EndsInDirectorySeparator(x) ? x : x + Path.DirectorySeparatorChar)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length)
        .Aggregate(new List<string>(), (list, next) =>
        { if (!list.Any(root => IsWithin(next, root))) list.Add(next); return list; }).ToArray();
}
