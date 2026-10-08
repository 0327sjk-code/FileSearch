// 文件用途：识别内置磁盘；排除 USB/1394/SD/MMC、可移除或外部热插拔设备。
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlashFind.Core.Indexing;

public sealed record IndexedDrive(string Root, string Label, string FileSystem, uint BusType,
    bool HardwareChecked, bool External);

public static class DriveDiscovery
{
    private const uint StorageQueryProperty = 0x002d1400;
    private const uint StorageGetHotplugInfo = 0x002d0c14;
    private const int BusTypeOffset = 28;
    private const int RemovableMediaOffset = 10;
    private static readonly uint[] ExternalBuses = { 4, 7, 9, 12, 13, 20 };

    public static IReadOnlyList<IndexedDrive> InternalDrives() => Discover().Where(x => !x.External).ToArray();

    public static IReadOnlyList<IndexedDrive> Discover()
    {
        var drives = new List<IndexedDrive>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!drive.IsReady) continue;
                bool external = drive.DriveType == DriveType.Removable, checkedHardware = false;
                uint bus = 0;
                using SafeFileHandle handle = CreateFile(@"\\.\" + drive.Name[..2], 0, 7,
                    IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var descriptor = new byte[4096];
                    var query = new byte[12];
                    if (DeviceIoControl(handle, StorageQueryProperty, query, query.Length, descriptor,
                        descriptor.Length, out int returned, IntPtr.Zero) && returned >= 36)
                    {
                        checkedHardware = true;
                        bus = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(BusTypeOffset));
                        external |= ExternalBuses.Contains(bus) || descriptor[RemovableMediaOffset] != 0;
                    }
                    var hotplug = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(hotplug, (uint)hotplug.Length);
                    if (DeviceIoControl(handle, StorageGetHotplugInfo, Array.Empty<byte>(), 0,
                        hotplug, hotplug.Length, out int hotplugReturned, IntPtr.Zero) && hotplugReturned >= 8)
                        external |= hotplug[4] != 0 || hotplug[6] != 0;
                }
                drives.Add(new(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveFormat,
                    bus, checkedHardware, external));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return drives;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
