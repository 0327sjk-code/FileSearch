// 文件用途：保存悬浮栏开关、停靠边及归一化位置，兼容已有硬盘配置。
namespace FlashFind.Core.Storage;

public sealed record DockSettings
{
    public bool Enabled { get; init; } = true;
    public string Edge { get; init; } = "right";
    public double Position { get; init; } = .26;
    public string Monitor { get; init; } = "";

    public DockSettings Normalize() => this with
    {
        Edge = Edge is "left" or "right" or "top" or "bottom" ? Edge : "right",
        Position = double.IsFinite(Position) ? Math.Clamp(Position, 0, 1) : .26,
        Monitor = Monitor ?? ""
    };
}
