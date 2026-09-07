using Dalamud.Plugin;
using ECommons.ExcelServices;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.GatheringPaths;
using Questionable.Model;
using Questionable.Model.Gathering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Questionable.Controller;

internal sealed class GatheringPointRegistry : IDisposable
{
    private readonly GatheringData _gatheringData;

    /// <summary>
    /// 🔴 <b>刻意不是 <c>readonly</c></b>：<see cref="Reload"/> 改成「在區域集合裡整份建好、
    /// 最後換一個參照上去」。原本是 <c>Clear()</c> 之後一條一條填回去，而讀取端
    /// （<c>GatheringController</c>、右鍵選單、日誌視窗）<b>沒有任何一個會跟寫入端上同一把鎖</b>，
    /// 所以那段期間查表會查到半空的登錄、迭代中的讀取端會擲 <c>InvalidOperationException</c>。
    /// 換成整份替換之後，讀取端看到的不是舊的就是新的，沒有中間狀態。
    /// </summary>
    private Dictionary<GatheringPointId, GatheringRoot> _gatheringPoints = [];
    private readonly ILogger<QuestRegistry> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly QuestRegistry _questRegistry;

    public GatheringPointRegistry(IDalamudPluginInterface pluginInterface,
        QuestRegistry questRegistry,
        GatheringData gatheringData,
        ILogger<QuestRegistry> logger)
    {
        _pluginInterface = pluginInterface;
        _questRegistry = questRegistry;
        _gatheringData = gatheringData;
        _logger = logger;

        _questRegistry.Reloaded += OnReloaded;
    }

    public void Dispose()
    {
        _questRegistry.Reloaded -= OnReloaded;
    }

    private void OnReloaded(object? sender, EventArgs e)
    {
        Reload();
    }

    /// <remarks>
    /// 📌 由 <c>QuestRegistry.Reloaded</c> 觸發時，呼叫端已經把 <c>QuestController._progressLock</c>
    /// 放掉了 —— 這裡的檔案列舉不會再擋住每一個 framework tick。
    /// </remarks>
    public void Reload()
    {
        Dictionary<GatheringPointId, GatheringRoot> gatheringPoints = [];

        LoadGatheringPointsFromAssembly(gatheringPoints);
        LoadGatheringPointsFromProjectDirectory(gatheringPoints);

        try
        {
            LoadFromDirectory(gatheringPoints,
                new(Path.Combine(_pluginInterface.ConfigDirectory.FullName, "GatheringPoints")));
        }
        catch(Exception e)
        {
            _logger.LogError(e,
                "Failed to load gathering points from user directory (some may have been successfully loaded)");
        }

        // 整份換上去；在這一行之前，讀取端看到的還是上一份完整的登錄。
        _gatheringPoints = gatheringPoints;

        _logger.LogInformation("Loaded {Count} gathering points in total", gatheringPoints.Count);
    }

    [Conditional("RELEASE")]
    private void LoadGatheringPointsFromAssembly(Dictionary<GatheringPointId, GatheringRoot> gatheringPoints)
    {
        _logger.LogInformation("Loading gathering points from assembly");

        foreach((ushort gatheringPointId, GatheringRoot gatheringRoot) in
            AssemblyGatheringLocationLoader.GetLocations())
        {
            if (gatheringRoot.Steps.Count >= 1)
            {
                foreach(GatheringNodeGroup group in gatheringRoot.Groups)
                {
                    foreach(GatheringNode node in group.Nodes)
                    {
                        foreach(GatheringLocation position in node.Locations)
                        {
                            gatheringRoot.Steps[0].Position = gatheringRoot.Steps[0].Position ?? position.Position;
                            gatheringRoot.Steps[0].Fly = gatheringRoot.Steps[0].Fly ?? true;
                            break;
                        }
                        break;
                    }
                    break;
                }
            }
            gatheringPoints[new(gatheringPointId)] = gatheringRoot;
        }

        _logger.LogInformation("Loaded {Count} gathering points from assembly", gatheringPoints.Count);
    }

    [Conditional("DEBUG")]
    private void LoadGatheringPointsFromProjectDirectory(Dictionary<GatheringPointId, GatheringRoot> gatheringPoints)
    {
        DirectoryInfo? solutionDirectory = _pluginInterface.AssemblyLocation.Directory?.Parent?.Parent;
        if (solutionDirectory != null)
        {
            DirectoryInfo pathProjectDirectory = new(Path.Combine(solutionDirectory.FullName, "GatheringPaths"));
            if (pathProjectDirectory.Exists)
            {
                try
                {
                    foreach(string expansionFolder in ExpansionData.ExpansionFolders.Values)
                    {
                        LoadFromDirectory(gatheringPoints,
                            new(Path.Combine(pathProjectDirectory.FullName, expansionFolder)));
                    }
                }
                catch(Exception e)
                {
                    gatheringPoints.Clear();
                    _logger.LogError(e, "Failed to load gathering points from project directory");
                }
            }
        }
    }

    private static void LoadGatheringPointFromStream(Dictionary<GatheringPointId, GatheringRoot> gatheringPoints, string fileName, Stream stream)
    {
        //_logger.LogTrace("Loading gathering point from '{FileName}'", fileName);
        GatheringPointId? gatheringPointId = ExtractGatheringPointIdFromName(fileName);
        if (gatheringPointId == null)
        {
            return;
        }

        GatheringRoot gatheringRoot = JsonSerializer.Deserialize<GatheringRoot>(stream)!;
        if (gatheringRoot.Steps.Count >= 1)
        {
            foreach(GatheringNodeGroup group in gatheringRoot.Groups)
            {
                foreach(GatheringNode node in group.Nodes)
                {
                    foreach(GatheringLocation position in node.Locations)
                    {
                        gatheringRoot.Steps[0].Position = gatheringRoot.Steps[0].Position ?? position.Position;
                        gatheringRoot.Steps[0].Fly = gatheringRoot.Steps[0].Fly ?? true;
                        break;
                    }
                    break;
                }
                break;
            }
        }
        gatheringPoints[gatheringPointId] = gatheringRoot;
    }

    private void LoadFromDirectory(Dictionary<GatheringPointId, GatheringRoot> gatheringPoints, DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            _logger.LogInformation("Not loading gathering points from {DirectoryName} (doesn't exist)", directory);
            return;
        }

        foreach(FileInfo fileInfo in directory.GetFiles("*.json"))
        {
            try
            {
                using FileStream stream = new(fileInfo.FullName, FileMode.Open, FileAccess.Read);
                LoadGatheringPointFromStream(gatheringPoints, fileInfo.Name, stream);
            }
            catch(Exception e)
            {
                throw new InvalidDataException($"Unable to load file {fileInfo.FullName}", e);
            }
        }

        foreach(DirectoryInfo childDirectory in directory.GetDirectories())
        {
            LoadFromDirectory(gatheringPoints, childDirectory);
        }
    }

    private static GatheringPointId? ExtractGatheringPointIdFromName(string resourceName)
    {
        string name = resourceName.Substring(0, resourceName.Length - ".json".Length);
        name = name.Substring(name.LastIndexOf('.') + 1);

        if (!name.Contains('_', StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = name.Split('_', 2);
        return GatheringPointId.FromString(parts[0]);
    }

    public bool TryGetGatheringPoint(GatheringPointId gatheringPointId, [NotNullWhen(true)] out GatheringRoot? gatheringRoot)
    {
        return _gatheringPoints.TryGetValue(gatheringPointId, out gatheringRoot);
    }

    public bool TryGetGatheringPointId(uint itemId, Job classJobId,
        [NotNullWhen(true)] out GatheringPointId? gatheringPointId)
    {
        if (classJobId == Job.MIN)
        {
            if (_gatheringData.TryGetMinerGatheringPointByItemId(itemId, out gatheringPointId))
            {
                return true;
            }

            gatheringPointId = _gatheringPoints
                .Where(x => x.Value.ExtraQuestItems.Contains(itemId))
                .Select(x => x.Key)
                .FirstOrDefault(x => _gatheringData.MinerGatheringPoints.Contains(x));
            return gatheringPointId != null;
        }
        else if (classJobId == Job.BTN)
        {
            if (_gatheringData.TryGetBotanistGatheringPointByItemId(itemId, out gatheringPointId))
            {
                return true;
            }

            gatheringPointId = _gatheringPoints
                .Where(x => x.Value.ExtraQuestItems.Contains(itemId))
                .Select(x => x.Key)
                .FirstOrDefault(x => _gatheringData.BotanistGatheringPoints.Contains(x));
            return gatheringPointId != null;
        }
        else
        {
            gatheringPointId = null;
            return false;
        }
    }
}
