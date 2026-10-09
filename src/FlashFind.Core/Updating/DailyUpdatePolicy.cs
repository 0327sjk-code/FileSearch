// 文件用途：每天本地时间 11:00、错过后补查和失败重试的纯调度规则。
namespace FlashFind.Core.Updating;

public static class DailyUpdatePolicy
{
    public static TimeOnly CheckTime { get; } = new(11, 0);
    public static TimeSpan RetryDelay { get; } = TimeSpan.FromMinutes(15);
    public static TimeSpan InstallIdleTime { get; } = TimeSpan.FromSeconds(3);

    public static bool ShouldCheck(DateTime localNow, DateOnly? completedDay, DateTimeOffset? retryAfter, DateTimeOffset utcNow, bool busy) =>
        !busy && TimeOnly.FromDateTime(localNow) >= CheckTime && completedDay != DateOnly.FromDateTime(localNow) &&
        (retryAfter is null || utcNow >= retryAfter);

    public static DateOnly? CompletedAfterSuccess(DateTime localNow, DateOnly? previous) =>
        TimeOnly.FromDateTime(localNow) >= CheckTime ? DateOnly.FromDateTime(localNow) : previous;
}
