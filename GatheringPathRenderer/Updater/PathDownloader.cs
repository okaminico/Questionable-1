using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;
using Dalamud.Plugin.Services;

namespace GatheringPathRenderer.Updater;

/// <summary>
/// 從 repo 下載採集路徑資料到使用者的 <see cref="RendererPlugin.PathsDirectory"/>。
/// <para>
/// 🔴 路徑檔 <b>不隨外掛出貨</b>（使用者裁決：「保留編輯後存檔　但那堆 json 打包成
/// 可從 gui 下載　不要放到 release」）。Release 版的 <c>PathsDirectory</c> 是
/// <c>&lt;pluginConfigs&gt;/Questionable/GatheringPaths</c>，啟動時只被
/// <c>CreateSubdirectory</c> 建成<b>空目錄</b> —— 內容唯一的來源就是設定視窗那顆
/// 手動按鈕。也就是說 <see cref="PathRepoBaseUrl"/> 這個常數決定了使用者實際看到
/// 的是誰的路徑資料。
/// </para>
/// <para>
/// 清單檔 <c>GatheringPathRenderer/Resources/paths_index.json</c> 由
/// <c>~/.claude/tools/questionable/gen_paths_index.py</c> 產生（含校準閘門）。
/// 🔴 <b>那支腳本裡的 <c>BASE_URL</c> 必須與這裡的 <see cref="PathRepoBaseUrl"/>
/// 逐字相同</b>：兩邊寫死成不同的 repo/分支，雜湊就永遠對不上、每次按更新都會把
/// 全部檔案重新下載一遍，而且不會有任何錯誤訊息（AutoDuty 踩過這個坑）。
/// </para>
/// </summary>
internal sealed class PathDownloader : IDisposable
{
    /// <summary>
    /// 路徑資料與其清單的來源。清單端（Python 腳本）與下載端（這裡）共用同一個值。
    /// </summary>
    internal const string PathRepoBaseUrl =
        "https://raw.githubusercontent.com/ffxiv-tc-port/Questionable/refs/heads/tc-7.20/";

    /// <summary>清單檔在 repo 裡的位置（「檔案相對路徑 → SHA-256 小寫十六進位」）。</summary>
    internal const string IndexRelativePath = "GatheringPathRenderer/Resources/paths_index.json";

    /// <summary>路徑檔在 repo 裡的根目錄；清單的鍵是相對於這裡的路徑。</summary>
    internal const string PathsRelativePath = "GatheringPaths/";

    /// <summary>
    /// 同時進行的下載數。587 個檔逐一序列下載大約要一分鐘；限制在個位數是為了
    /// 不要讓 raw.githubusercontent 把我們當成濫用流量。
    /// </summary>
    private const int MaxConcurrency = 4;

    internal enum Phase
    {
        Idle,
        FetchingIndex,
        Scanning,
        Downloading,
        Done,
        Failed,
    }

    private readonly RendererPlugin _plugin;
    private readonly IPluginLog _pluginLog;
    private readonly IFramework _framework;

    private readonly SocketsHttpHandler _handler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = new HappyEyeballsCallback().ConnectCallback,
    };

    private readonly HttpClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _resultLock = new();
    private readonly List<string> _failures = [];

    private Task? _task;

    // ⚠️ 這些欄位由背景工作寫、由 ImGui 繪製執行緒讀。int 用 Interlocked/Volatile，
    //    字串是參考指派（本身具原子性），清單一律在 _resultLock 底下操作。
    private volatile Phase _phase = Phase.Idle;
    private volatile string _statusText = string.Empty;
    /// <summary>
    /// 清單上有幾個檔。<c>-1</c> ＝ <b>還沒抓過清單，不知道</b>。
    /// 📌 刻意不用 0 當初始值：UI 上「0」會被讀成「確認遠端沒有資料」，
    /// 與「還沒問過」是完全不同的兩件事。
    /// </summary>
    private int _indexCount = -1;

    private int _total;
    private int _processed;
    private int _downloaded;
    private int _upToDate;
    private int _locallyModified;
    private int _failed;

    internal PathDownloader(RendererPlugin plugin, IPluginLog pluginLog, IFramework framework)
    {
        _plugin = plugin;
        _pluginLog = pluginLog;
        _framework = framework;
        _client = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal Phase CurrentPhase => _phase;
    internal string StatusText => _statusText;

    /// <summary>清單上的檔案數；<c>-1</c> 代表還沒抓過清單（不是「遠端有 0 個」）。</summary>
    internal int IndexCount => Volatile.Read(ref _indexCount);

    internal int Total => Volatile.Read(ref _total);
    internal int Processed => Volatile.Read(ref _processed);
    internal int Downloaded => Volatile.Read(ref _downloaded);
    internal int UpToDate => Volatile.Read(ref _upToDate);
    internal int LocallyModified => Volatile.Read(ref _locallyModified);
    internal int Failed => Volatile.Read(ref _failed);

    internal bool IsRunning => _task is { IsCompleted: false };

    internal List<string> SnapshotFailures()
    {
        lock (_resultLock)
            return [.._failures];
    }

    /// <summary>
    /// 開始一次下載。<paramref name="overwriteModified"/> 為 false（預設）時，
    /// <b>只下載本機沒有的檔</b>；本機存在但內容與清單不符的檔會被算進
    /// <see cref="LocallyModified"/> 並列出來，<b>不覆蓋</b> ——
    /// 因為在 Release 版底下那個目錄同時也是編輯器的存檔位置，
    /// 「內容不一樣」既可能是版本舊了、也可能是使用者自己改的，離線分不出來。
    /// </summary>
    internal void Start(bool overwriteModified)
    {
        if (IsRunning)
            return;

        lock (_resultLock)
            _failures.Clear();
        Volatile.Write(ref _total, 0);
        Volatile.Write(ref _processed, 0);
        Volatile.Write(ref _downloaded, 0);
        Volatile.Write(ref _upToDate, 0);
        Volatile.Write(ref _locallyModified, 0);
        Volatile.Write(ref _failed, 0);
        _statusText = string.Empty;
        _phase = Phase.FetchingIndex;

        _task = Task.Run(() => RunAsync(overwriteModified, _cts.Token));
    }

    private async Task RunAsync(bool overwriteModified, CancellationToken token)
    {
        try
        {
            _pluginLog.Information(
                $"[GatheringPaths] 開始更新路徑資料 (overwriteModified={overwriteModified}, 來源={PathRepoBaseUrl})");

            Dictionary<string, string> index;
            try
            {
                string indexJson = await _client.GetStringAsync(PathRepoBaseUrl + IndexRelativePath, token)
                    .ConfigureAwait(false);
                index = JsonSerializer.Deserialize<Dictionary<string, string>>(indexJson) ?? [];
            }
            catch (Exception e)
            {
                Fail($"取得清單失敗：{e.Message}");
                _pluginLog.Information($"[GatheringPaths] 取得清單失敗 ({PathRepoBaseUrl + IndexRelativePath}): {e}");
                return;
            }

            Volatile.Write(ref _indexCount, index.Count);
            if (index.Count == 0)
            {
                Fail("清單是空的，沒有可下載的路徑資料");
                return;
            }

            _phase = Phase.Scanning;
            DirectoryInfo root = _plugin.PathsDirectory;
            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.FullName));

            List<(string Key, string Hash, string LocalPath)> toDownload = [];
            foreach ((string key, string expectedHash) in index)
            {
                token.ThrowIfCancellationRequested();

                // 清單是從網路拿的，鍵會被直接接到本機路徑後面 —— 先擋掉能逃出目標目錄的形狀。
                if (!IsSafeRelativeKey(key))
                {
                    AddFailure($"{key}（清單的鍵不是安全的相對路徑，已略過）");
                    continue;
                }

                string localPath = Path.GetFullPath(
                    Path.Combine(rootFull, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!localPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    AddFailure($"{key}（解析後落在目標目錄之外，已略過）");
                    continue;
                }

                if (File.Exists(localPath))
                {
                    string localHash;
                    try
                    {
                        localHash = HashFile(localPath);
                    }
                    catch (Exception e)
                    {
                        AddFailure($"{key}（讀不到本機檔：{e.Message}）");
                        continue;
                    }

                    if (string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _upToDate);
                        continue;
                    }

                    if (!overwriteModified)
                    {
                        Interlocked.Increment(ref _locallyModified);
                        continue;
                    }
                }

                toDownload.Add((key, expectedHash, localPath));
            }

            Volatile.Write(ref _total, toDownload.Count);
            if (toDownload.Count == 0)
            {
                Finish();
                return;
            }

            _phase = Phase.Downloading;
            using SemaphoreSlim gate = new(MaxConcurrency);
            await Task.WhenAll(toDownload.Select(async item =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await DownloadOneAsync(item.Key, item.Hash, item.LocalPath, token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Increment(ref _processed);
                    gate.Release();
                }
            })).ConfigureAwait(false);

            Finish();
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Idle;
            _statusText = "已取消";
        }
        catch (Exception e)
        {
            Fail($"更新失敗：{e.Message}");
            _pluginLog.Information($"[GatheringPaths] 更新路徑資料時發生未預期的例外: {e}");
        }
    }

    private async Task DownloadOneAsync(string key, string expectedHash, string localPath, CancellationToken token)
    {
        string url = PathRepoBaseUrl + PathsRelativePath + EncodePath(key);
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);

            // ⚠️ 一定要用 bytes 不要用 string：`ReadAsStringAsync` + `WriteAllTextAsync`
            //    會在來源有 BOM 時把 BOM 吃掉，寫回去的檔就與清單算雜湊的基準不同，
            //    結果是每次更新都重新下載同一批檔，而且完全不報錯。
            string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                // 最可能的成因是清單過期（repo 的路徑檔更新了但沒重跑產生器）。
                // 寧可失敗也不要寫下去 —— 寫下去只會在下次更新時再次「不符」，
                // 變成永遠停不下來的重複下載。
                AddFailure($"{key}（下載內容的雜湊與清單不符，清單可能過期）");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes, token).ConfigureAwait(false);
            Interlocked.Increment(ref _downloaded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            AddFailure($"{key}（{e.Message}）");
        }
    }

    /// <summary>
    /// 逐段做 URL 編碼。⚠️ 必須逐段：路徑裡有空格（<c>11_Jadeite Thick_BTN.json</c>）
    /// 也有點（<c>2.x - A Realm Reborn</c>），整條一起編碼會把分隔用的 <c>/</c> 也吃掉。
    /// </summary>
    private static string EncodePath(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static bool IsSafeRelativeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        if (!key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key.Contains('\\') || key.Contains(':') || key.StartsWith('/'))
            return false;

        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
            if (segment.IndexOfAny(invalid) >= 0)
                return false;
        }

        return true;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void AddFailure(string message)
    {
        Interlocked.Increment(ref _failed);
        lock (_resultLock)
        {
            if (_failures.Count < 200)
                _failures.Add(message);
        }
    }

    private void Fail(string message)
    {
        _phase = Phase.Failed;
        _statusText = message;
    }

    private void Finish()
    {
        _phase = Phase.Done;
        _statusText =
            $"新下載 {Downloaded}、已是最新 {UpToDate}、本機已修改未覆蓋 {LocallyModified}、失敗 {Failed}";

        // 📌 使用者跑 LogLevel 1（Serilog 的 Debug 門檻；Information 是 2），盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒 —— 要能被回報的診斷寫這一級。
        _pluginLog.Information($"[GatheringPaths] 更新完成：{_statusText}");
        foreach (string failure in SnapshotFailures())
            _pluginLog.Information($"[GatheringPaths] 失敗：{failure}");

        if (Downloaded > 0)
        {
            // 🔴 Reload() 會清空並重建 _gatheringLocations，而那份清單同時被
            //    RendererPlugin.Draw()（UI 執行緒）走訪 —— 從背景工作直接呼叫等於
            //    邊走訪邊改集合。一律丟回框架執行緒。
            _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    _plugin.Reload();
                }
                catch (Exception e)
                {
                    _pluginLog.Information($"[GatheringPaths] 下載後重新載入失敗: {e}");
                }
            });
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();

            // ⚠️ 一定要等背景工作真的停下來再 Dispose HttpClient：不等的話，
            //    工作可能正在 GetAsync 中途，拿到的是 ObjectDisposedException，
            //    然後在 catch 裡去用同樣已經被拆掉的 IPluginLog —— 那個例外沒有人
            //    觀察得到，會變成完全靜默的 unload 期怪象。
            //    工作全程 ConfigureAwait(false)、不捕捉同步內容，Wait 不會死結。
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            _pluginLog.Information($"[GatheringPaths] 停止下載工作時發生例外: {e}");
        }

        _cts.Dispose();
        _client.Dispose();
        _handler.Dispose();
    }
}
