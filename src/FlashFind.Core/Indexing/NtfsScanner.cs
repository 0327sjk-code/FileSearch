// 文件用途：只读 NTFS MFT 枚举；没有权限或不支持时交给目录扫描。
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlashFind.Core.Indexing;

public sealed record MftNode(ulong Id, ulong ParentId, string Name, bool Directory);

public sealed class NtfsScanner
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint EnumUsnData = 0x000900b3;
    private const int ErrorEndOfFile = 38;
    private const int OutputBufferSize = 1024 * 1024;
    private const int MaximumParentDepth = 1024;
    private const ulong FileReferenceMask = 0x0000ffffffffffff;
    private const ulong RootRecordNumber = 5;

    public IReadOnlyList<FileEntry> Scan(string root, IReadOnlyList<string> excluded,
        Action<ScanProgress> progress, CancellationToken cancellation)
    {
        var drive = new DriveInfo(root);
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            !Path.TrimEndingDirectorySeparator(root).Equals(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("NTFS 快速索引仅适用于完整的本地 NTFS 磁盘。");
        using SafeFileHandle handle = CreateFile(@"\\.\" + drive.Name[..2], GenericRead,
            ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Dictionary<ulong, MftNode> nodes = ReadNodes(handle, root, progress, cancellation);
        return ResolvePaths(nodes, root, excluded, cancellation);
    }

    private static Dictionary<ulong, MftNode> ReadNodes(SafeFileHandle handle, string root,
        Action<ScanProgress> progress, CancellationToken cancellation)
    {
        var nodes = new Dictionary<ulong, MftNode>();
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        var output = new byte[OutputBufferSize];
        int lastReported = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            bool success = DeviceIoControl(handle, EnumUsnData, input, input.Length,
                output, output.Length, out int returned, IntPtr.Zero);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorEndOfFile) break;
                throw new Win32Exception(error);
            }
            if (returned < sizeof(ulong)) throw new InvalidDataException("MFT 返回数据不完整。");
            foreach (MftNode node in ParseRecords(output.AsSpan(8, returned - 8))) nodes[node.Id] = node;
            output.AsSpan(0, 8).CopyTo(input);
            if (nodes.Count - lastReported >= 20000)
            {
                progress(new(root, nodes.Count, 0, "NTFS 快速索引"));
                lastReported = nodes.Count;
            }
        }
        return nodes;
    }

    public static IReadOnlyList<MftNode> ParseRecords(ReadOnlySpan<byte> records)
    {
        const int MinimumRecordLength = 60, AttributesOffset = 52, NameLengthOffset = 56, NameOffsetOffset = 58;
        var result = new List<MftNode>();
        int offset = 0;
        while (offset < records.Length)
        {
            ReadOnlySpan<byte> remaining = records[offset..];
            if (remaining.Length < MinimumRecordLength) throw new InvalidDataException("USN 记录被截断。");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            if (length < MinimumRecordLength || length > remaining.Length) throw new InvalidDataException("USN 记录长度异常。");
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]);
            if (major != 2) throw new NotSupportedException($"USN 版本 {major} 使用目录扫描。");
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(remaining[NameLengthOffset..]);
            ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(remaining[NameOffsetOffset..]);
            if ((nameLength & 1) != 0 || nameOffset < MinimumRecordLength || nameOffset + nameLength > length)
                throw new InvalidDataException("USN 文件名边界异常。");
            string name = Encoding.Unicode.GetString(remaining.Slice(nameOffset, nameLength));
            result.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(remaining[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(remaining[16..]), name,
                (BinaryPrimitives.ReadUInt32LittleEndian(remaining[AttributesOffset..]) & (uint)FileAttributes.Directory) != 0));
            offset += checked((int)length);
        }
        return result;
    }

    public static IReadOnlyList<FileEntry> ResolvePaths(IReadOnlyDictionary<ulong, MftNode> nodes,
        string root, IReadOnlyList<string> excluded, CancellationToken cancellation = default)
    {
        var paths = new Dictionary<ulong, string>();
        foreach (MftNode node in nodes.Values)
            if ((node.Id & FileReferenceMask) == RootRecordNumber) paths[node.Id] = root;
        var result = new List<FileEntry>(nodes.Count);
        foreach (MftNode node in nodes.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((node.Id & FileReferenceMask) == RootRecordNumber) continue;
            string? parent = ResolveDirectory(node.ParentId, nodes, paths, root);
            if (parent is null || node.Name.Contains('\\') || node.Name.Contains('/')) continue;
            string path = Path.Combine(parent, node.Name);
            if (excluded.Any(value => PathRules.IsWithin(path, value))) continue;
            if (parent.Equals(root, StringComparison.OrdinalIgnoreCase) && node.Name.StartsWith('$')) continue;
            result.Add(FileEntry.FromPath(path, node.Directory));
        }
        return result;
    }

    private static string? ResolveDirectory(ulong id, IReadOnlyDictionary<ulong, MftNode> nodes,
        Dictionary<ulong, string> cache, string root)
    {
        var chain = new List<MftNode>();
        var visited = new HashSet<ulong>();
        string? path;
        while (!cache.TryGetValue(id, out path))
        {
            if ((id & FileReferenceMask) == RootRecordNumber) { path = root; break; }
            if (!visited.Add(id) || chain.Count >= MaximumParentDepth || !nodes.TryGetValue(id, out MftNode? node) || !node.Directory)
                return null;
            chain.Add(node); id = node.ParentId;
        }
        for (int index = chain.Count - 1; index >= 0; index--)
        { path = Path.Combine(path!, chain[index].Name); cache[chain[index].Id] = path; }
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
