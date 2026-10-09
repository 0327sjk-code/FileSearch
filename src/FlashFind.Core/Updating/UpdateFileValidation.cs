// 文件用途：安装前再次核验完整文件、SHA256 和 Windows 程序身份。
using System.Diagnostics;
using System.Security.Cryptography;

namespace FlashFind.Core.Updating;

public static class UpdateFileValidation
{
    public static void Verify(string path, ReleaseManifest manifest)
    {
        Version expected = manifest.Validate();
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != manifest.Size)
            throw new InvalidDataException("更新文件不完整。");
        using FileStream stream = File.OpenRead(path);
        string digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!digest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件校验失败。");
        stream.Position = 0;
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("更新文件不是 Windows 程序。");
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        Version actual = new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (info.ProductName != UpdateProduct.ProductName || info.CompanyName != UpdateProduct.CompanyName || actual != expected)
            throw new InvalidDataException("更新程序身份或版本不匹配。");
    }
}
