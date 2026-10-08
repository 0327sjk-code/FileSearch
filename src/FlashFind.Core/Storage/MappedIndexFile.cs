// 文件用途：只读映射的边界检查和生命周期；旧结果仍引用映射时不会提前关闭。
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlashFind.Core.Storage;

internal sealed unsafe class MappedIndexFile
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    public long Length { get; }

    public MappedIndexFile(FileStream file)
    {
        Length = file.Length;
        _mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
        try
        {
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
        }
        catch { _mapping.Dispose(); throw; }
    }

    ~MappedIndexFile()
    {
        if (_pointer == null) return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); _mapping.Dispose();
    }

    public ReadOnlySpan<byte> Bytes(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new InvalidDataException("直接查询索引的数据越界。");
        return new ReadOnlySpan<byte>(_pointer + offset, length);
    }

    public ReadOnlySpan<char> Chars(long offset, int length) => MemoryMarshal.Cast<byte, char>(Bytes(offset, checked(length * sizeof(char))));
    public ReadOnlySpan<int> Integers(long offset, int count) => MemoryMarshal.Cast<byte, int>(Bytes(offset, checked(count * sizeof(int))));
    public int ReadInt32(long offset) => Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(Bytes(offset, sizeof(int))));
    public long ReadInt64(long offset) => Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(Bytes(offset, sizeof(long))));
}
