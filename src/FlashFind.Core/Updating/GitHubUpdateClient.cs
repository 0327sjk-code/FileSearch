// 文件用途：匿名读取公开 GitHub 清单，流式下载并验证；内存仅保留一个 64KB 缓冲区。
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlashFind.Core.Updating;

public sealed class GitHubUpdateClient(HttpClient http, Action<string, ReleaseManifest>? verifyArtifact = null)
{
    private const int BufferBytes = 64 * 1024;
    private const int MaximumManifestBytes = 16 * 1024;
    private readonly Action<string, ReleaseManifest> _verifyArtifact = verifyArtifact ?? UpdateFileValidation.Verify;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ReleaseManifest> CheckAsync(CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await http.GetAsync(UpdateProduct.ManifestUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumManifestBytes) throw new InvalidDataException("更新清单过大。");
        await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var payload = new MemoryStream();
        byte[] buffer = new byte[MaximumManifestBytes + 1];
        int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            if (payload.Length + count > MaximumManifestBytes) throw new InvalidDataException("更新清单过大。");
            payload.Write(buffer, 0, count);
        }
        ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(payload.ToArray(), Json) ?? throw new InvalidDataException("更新清单为空。");
        manifest.Validate(); return manifest;
    }

    public async Task DownloadAsync(ReleaseManifest manifest, string destination, IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        manifest.Validate();
        string partial = destination + ".partial";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using HttpResponseMessage response = await http.GetAsync(new Uri(manifest.ExecutableUrl), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != manifest.Size) throw new InvalidDataException("下载文件大小与清单不一致。");
            await using (Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[BufferBytes]; long total = 0; int previousPercent = -1, count;
                while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > manifest.Size) throw new InvalidDataException("下载文件超过发布大小。");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    int percent = (int)(total * 100 / manifest.Size);
                    if (percent != previousPercent) { progress?.Report(percent); previousPercent = percent; }
                }
                if (total != manifest.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载不完整或校验失败。");
                await target.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            _verifyArtifact(partial, manifest);
            File.Move(partial, destination);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }
}
