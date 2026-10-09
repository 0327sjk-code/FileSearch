// 文件用途：每日调度、清单约束、网络失败、流式下载及文件校验的后台回归。
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using FlashFind.Core.Updating;
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Updating;

public static class UpdateProtocolChecks
{
    public static async Task<IReadOnlyDictionary<string, bool>> RunAsync(string output)
    {
        var checks = new Dictionary<string, bool>();
        var before = new DateTime(2026, 10, 9, 10, 59, 59); DateTime now = before.AddSeconds(1);
        DateOnly day = DateOnly.FromDateTime(now); DateTimeOffset utc = new(now, TimeSpan.FromHours(8));
        Check("11点之前不自动检查", !Due(before)); Check("11点当时自动检查", Due(now));
        Check("下午启动补查", Due(now.AddHours(7))); Check("完成当天后不重复", !Due(now, day));
        Check("第二天11点再次检查", Due(now.AddDays(1), day)); Check("凌晨不提前检查", !Due(now.AddDays(1).Date, day));
        Check("忙碌不并发检查", !DailyUpdatePolicy.ShouldCheck(now, null, null, utc, true));
        Check("失败未到重试时间不检查", !DailyUpdatePolicy.ShouldCheck(now, null, utc.AddMinutes(15), utc, false));
        Check("失败15分钟后重试", DailyUpdatePolicy.ShouldCheck(now, null, utc.AddMinutes(15), utc.AddMinutes(15), false));
        Check("上午手动成功不取消11点检查", DailyUpdatePolicy.CompletedAfterSuccess(before, null) is null);
        Check("11点后手动成功记为当天完成", DailyUpdatePolicy.CompletedAfterSuccess(now, null) == day);
        Check("跨日与本地日期独立于UTC", DailyUpdatePolicy.ShouldCheck(now.AddDays(1), day, null, utc, false));
        Check("版本正确比较", UpdateProduct.ParseVersion("1.10.0") > UpdateProduct.ParseVersion("1.2.9"));
        foreach (string invalid in new[] { "", "v1.2.0", "1.2", "1.2.0.0", "1.2.0-beta", "-1.2.0", "1.2.999999999999" })
            Check("拒绝无效版本 " + invalid, Throws<InvalidDataException>(() => UpdateProduct.ParseVersion(invalid)));

        string statePath = Path.Combine(output, "automatic-update.json");
        new DailyUpdateState(day).Save(statePath); Check("每日状态保存并恢复", DailyUpdateState.Load(statePath).CompletedDay == day);
        File.WriteAllText(statePath, "corrupt"); Check("损坏调度状态安全补查", DailyUpdateState.Load(statePath).CompletedDay is null);
        byte[] payload = Enumerable.Range(0, 230001).Select(index => (byte)(index % 251)).ToArray();
        string digest = Convert.ToHexString(SHA256.HashData(payload));
        var manifest = new ReleaseManifest("1.2.0", UpdateProduct.ExecutableUri("1.2.0").AbsoluteUri, digest, payload.Length);
        Check("发布清单版本与固定仓库匹配", manifest.Validate() == new Version(1, 2, 0));
        foreach (string url in new[] { "http://github.com/0327sjk-code/FileSearch/releases/download/v1.2.0/文件搜索.exe",
                     "https://example.com/files.exe", UpdateProduct.ExecutableUri("1.2.1").AbsoluteUri,
                     manifest.ExecutableUrl + "?redirect=1", manifest.ExecutableUrl.Replace("FileSearch", "SuperRecording", StringComparison.Ordinal) })
            Check("拒绝跨仓库或跨版本地址 " + url, Throws<InvalidDataException>(() => (manifest with { ExecutableUrl = url }).Validate()));
        Check("拒绝超大程序", Throws<InvalidDataException>(() => (manifest with { Size = UpdateProduct.MaximumExecutableBytes + 1 }).Validate()));
        Check("拒绝损坏校验码", Throws<InvalidDataException>(() => (manifest with { Sha256 = "xyz" }).Validate()));
        bool validated = false;
        using var handler = new FixtureHandler(manifest, payload); using var http = new HttpClient(handler);
        var client = new GitHubUpdateClient(http, (path, release) => validated = File.ReadAllBytes(path).SequenceEqual(payload) && release == manifest);
        Check("清单可读取且无需凭证", await client.CheckAsync() == manifest && !handler.SawAuthorization);
        string downloaded = Path.Combine(output, "download-fixture.exe"); if (File.Exists(downloaded)) File.Delete(downloaded);
        var progress = new InlineProgress(); await client.DownloadAsync(manifest, downloaded, progress);
        Check("完整流式下载并验证", validated && File.ReadAllBytes(downloaded).SequenceEqual(payload));
        Check("下载进度达到100", progress.Percent == 100);
        await RejectedAsync("拒绝错误哈希并删除半成品", client, manifest with { Sha256 = new string('0', 64) });
        handler.Data = payload[..^1]; await RejectedAsync("拒绝截断下载", client, manifest);
        handler.Data = payload.Concat(new byte[] { 1 }).ToArray(); await RejectedAsync("拒绝超长下载", client, manifest);
        handler.Status = HttpStatusCode.ServiceUnavailable; await RejectedAsync("网络失败保留原程序", client, manifest);
        handler.Status = HttpStatusCode.OK; handler.Data = payload;
        string cancelled = Path.Combine(output, "cancelled.exe");
        try { await client.DownloadAsync(manifest, cancelled, cancellation: new CancellationToken(true)); Check("取消下载抛出取消异常", false); }
        catch (OperationCanceledException) { Check("取消下载不产生文件", !File.Exists(cancelled) && !File.Exists(cancelled + ".partial")); }
        handler.OversizedManifest = true;
        try { await client.CheckAsync(); Check("过大清单被拒绝", false); }
        catch (InvalidDataException) { Check("过大清单被拒绝", true); }
        string actual = Environment.ProcessPath!;
        var own = new ReleaseManifest(UpdateProduct.DisplayVersion, UpdateProduct.ExecutableUri(UpdateProduct.DisplayVersion).AbsoluteUri,
            UpdateDiagnostics.HashFile(actual), new FileInfo(actual).Length);
        UpdateFileValidation.Verify(actual, own); Check("发布包实际PE身份版本哈希校验", true);
        var ui = new UpdateViewModel(() => { });
        Check("保留手动更新入口", ui.CheckCommand.CanExecute(null)); ui.Checking();
        Check("检查期间禁止重复点击", !ui.CheckCommand.CanExecute(null)); ui.Downloading("1.2.0", 42);
        Check("下载进度中文显示", ui.ButtonText == "下载中 42%" && ui.HasStatus); ui.Ready("1.2.0");
        Check("待安装继续禁止重复检查", !ui.CheckCommand.CanExecute(null)); ui.Complete("已是最新版本");
        Check("结束后恢复手动检查", ui.CheckCommand.CanExecute(null));
        return checks;

        bool Due(DateTime time, DateOnly? completed = null) => DailyUpdatePolicy.ShouldCheck(time, completed, null, utc, false);
        void Check(string name, bool passed) { checks.Add(name, passed); if (!passed) throw new InvalidOperationException("更新验证失败：" + name); }
        async Task RejectedAsync(string name, GitHubUpdateClient source, ReleaseManifest release)
        {
            string path = Path.Combine(output, Guid.NewGuid().ToString("N") + ".exe");
            try { await source.DownloadAsync(release, path); Check(name, false); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException)
            { Check(name, !File.Exists(path) && !File.Exists(path + ".partial")); }
        }
    }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    private sealed class InlineProgress : IProgress<int> { public int Percent { get; private set; } public void Report(int value) => Percent = value; }
    private sealed class FixtureHandler(ReleaseManifest manifest, byte[] data) : HttpMessageHandler
    {
        public byte[] Data { get; set; } = data;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool SawAuthorization { get; private set; }
        public bool OversizedManifest { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); SawAuthorization |= request.Headers.Authorization is not null;
            byte[] content = request.RequestUri == UpdateProduct.ManifestUri
                ? OversizedManifest ? new byte[20 * 1024] : JsonSerializer.SerializeToUtf8Bytes(manifest) : Data;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(content), RequestMessage = request });
        }
    }
}
