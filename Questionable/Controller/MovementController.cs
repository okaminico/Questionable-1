using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Questionable.Controller.NavigationOverrides;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Common.Converter;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
namespace Questionable.Controller;

internal sealed class MovementController
(
    NavmeshIpc navmeshIpc,
    IClientState clientState,
    GameFunctions gameFunctions,
    ChatFunctions chatFunctions,
    ICondition condition,
    MovementOverrideController movementOverrideController,
    IObjectTable objectTable,
    AetheryteData aetheryteData,
    ICommandManager commandManager,
    IServiceProvider serviceProvider,
    Configuration configuration,
    ILogger<MovementController> logger) : IDisposable
{
    public ICommandManager CommandManager { get; } = commandManager;
    public const float DefaultVerticalInteractionDistance = 1.95f;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task<List<Vector3>>? _pathfindTask;

    public bool IsNavmeshReady
    {
        get
        {
            try
            {
                return navmeshIpc.IsReady;
            }
            catch(IpcNotReadyError)
            {
                return false;
            }
        }
    }

    public bool IsPathRunning
    {
        get
        {
            try
            {
                return navmeshIpc.IsPathRunning;
            }
            catch(IpcNotReadyError)
            {
                return false;
            }
        }
    }

    public bool IsPathfinding => _pathfindTask is { IsCompleted: false };

    /// <summary><see cref="IsPathRunning"/> 的每幀快照，見 <see cref="RefreshNavmeshSnapshot"/>。</summary>
    private volatile bool _pathRunningSnapshot;

    /// <summary><see cref="IsPathfinding"/> 的每幀快照，見 <see cref="RefreshNavmeshSnapshot"/>。</summary>
    private volatile bool _pathfindingSnapshot;

    /// <summary>
    /// 「vnavmesh 現在有沒有在跑路徑」的<b>快照</b>——讀這個不會打 IPC。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>存在的理由</b>：<see cref="IsPathRunning"/> 的 getter 是一次跨外掛 IPC
    /// （<c>NavmeshIpc._pathIsRunning.InvokeFunc()</c>）。<c>QuestController.UpdateCurrentQuestLocked</c>
    /// 在<b>持有 <c>_progressLock</c> 的時候</b>讀它，等於在我們每一幀都會拿的鎖裡面跑 vnavmesh 的碼，
    /// 也就是把對方的鎖排在我們的鎖後面。
    /// <para>
    /// ⚠️ 它是<b>屬性</b>不是方法呼叫，所以 <c>lock_io_scan.py</c> 的呼叫圖看不到它——
    /// 那支工具跟的是 <c>Name(</c> 形狀的呼叫點，getter 沒有括號。這一筆是人工逐行讀出來的。
    /// </para>
    /// <para>
    /// 📌 新鮮度：<c>DalamudInitializer.FrameworkUpdate</c> 的順序是
    /// <c>UpdateLease()</c> → <b><see cref="RefreshNavmeshSnapshot"/>()</b> →
    /// <c>_partyWatchDog.Update()</c> → <c>_questController.Update()</c> →
    /// <c>_movementController.Update()</c>
    /// ⇒ <b>QuestController 讀到的是同一幀剛取樣的值</b>，不是上一幀的。
    /// 🔴 那一行必須留在 <c>_questController.Update()</c> <b>之前</b>；被移到後面的話語意會退化成
    /// 「晚一幀」（仍然安全，只是舊一格），而不是壞掉。
    /// </para>
    /// <para>
    /// 📌 幀內一致性：<see cref="Stop"/> 與 <see cref="ResetPathfinding"/> 會<b>當場</b>把快照
    /// 設回 <see langword="false"/>（純本機寫入、不打 IPC），所以「鎖內停止移動之後再判斷還在不在
    /// 移動」的結果與改動前相同——不會因為改讀快照而卡在「Path is running」。
    /// 反過來，開始尋路／開始移動時也當場設成 <see langword="true"/>。
    /// </para>
    /// </remarks>
    public bool IsPathRunningSnapshot => _pathRunningSnapshot;

    /// <inheritdoc cref="IsPathRunningSnapshot"/>
    public bool IsPathfindingSnapshot => _pathfindingSnapshot;

    /// <summary>
    /// 每幀取樣一次 vnavmesh 的路徑狀態。<b>只能在 framework 執行緒、而且不持有任何鎖時呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不擲例外</b>：它排在 <c>_questController.Update()</c> 之前，而
    /// <c>DalamudInitializer.FrameworkUpdate</c> 裡任何一支擲例外都會讓它之後的程式碼當幀不執行。
    /// </remarks>
    public void RefreshNavmeshSnapshot()
    {
        try
        {
            _pathRunningSnapshot = IsPathRunning;
        }
        catch(Exception e)
        {
            _pathRunningSnapshot = false;
            logger.LogDebug(e, "Could not read navmesh path state, assuming no path is running");
        }

        _pathfindingSnapshot = IsPathfinding;
    }
    public DestinationData? Destination { get; set; }
    public DateTime MovementStartedAt { get; private set; } = DateTime.Now;
    public int BuiltNavmeshPercent => navmeshIpc.GetBuildProgress();

    public void Dispose()
    {
        Stop();
    }

    public void Update()
    {
        if (_pathfindTask != null && Destination != null)
        {
            if (_pathfindTask.IsCompletedSuccessfully)
            {
                logger.LogInformation("Pathfinding complete, got {Count} points", _pathfindTask.Result.Count);
                if (_pathfindTask.Result.Count == 0)
                {
                    //_commandManager.ProcessCommand("/vnav rebuild");
                    ResetPathfinding();
                    throw new PathfindingFailedException();
                }

                List<Vector3> navPoints = _pathfindTask.Result.Skip(1).ToList();
                Vector3 start = objectTable[0]?.Position ?? navPoints[0];
                if (Destination.IsFlying && !condition[ConditionFlag.InFlight] && condition[ConditionFlag.Mounted])
                {
                    if (IsOnFlightPath(start) || navPoints.Any(IsOnFlightPath))
                    {
                        unsafe
                        {
                            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
                        }
                    }
                }

                if (!Destination.IsFlying)
                {
                    (navPoints, bool recalculateNavmesh) = movementOverrideController.AdjustPath(navPoints);
                    if (recalculateNavmesh && Destination.ShouldRecalculateNavmesh())
                    {
                        Destination.NavmeshCalculations++;
                        Destination.PartialRoute.AddRange(navPoints);
                        logger.LogInformation("Running navmesh recalculation with fudged point ({From} to {To})",
                            navPoints.Last(), Destination.Position);

                        _cancellationTokenSource = new();
                        _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));
                        _pathfindTask =
                            navmeshIpc.Pathfind(navPoints.Last(), Destination.Position, Destination.IsFlying,
                                _cancellationTokenSource.Token);
                        return;
                    }
                }

                navPoints = Destination.PartialRoute.Concat(navPoints).ToList();
                logger.LogInformation("Navigating via route: [{Route}]",
                    string.Join(" → ",
                        _pathfindTask.Result.Select(x => x.ToString("G", CultureInfo.InvariantCulture))));

                navmeshIpc.MoveTo(navPoints, Destination.IsFlying);
                _pathRunningSnapshot = true;
                MovementStartedAt = DateTime.Now;

                ResetPathfinding();
            }
            else if (_pathfindTask.IsCompleted)
            {
                logger.LogWarning("Unable to complete pathfinding task");
                //_commandManager.ProcessCommand("/vnav rebuild");
                ResetPathfinding();
                throw new PathfindingFailedException();
            }
        }

        if (!serviceProvider.GetRequiredService<QuestController>().IsQuestingActive)
            return;

        if (IsPathRunning && Destination != null)
        {
            if (gameFunctions.IsLoadingScreenVisible())
            {
                logger.LogInformation("Stopping movement, loading screen visible");
                Stop();
                return;
            }

            if (Destination is { IsFlying: true } && condition[ConditionFlag.Swimming])
            {
                logger.LogInformation("Flying but swimming, restarting as non-flying path...");
                Restart(Destination);
                return;
            }
            else if (Destination is { IsFlying: true } && !condition[ConditionFlag.Mounted])
            {
                logger.LogInformation("Flying but not mounted, restarting as non-flying path...");
                Restart(Destination);
                return;
            }

            Vector3 localPlayerPosition = objectTable[0]?.Position ?? Vector3.Zero;
            if (Destination.MovementType == EMovementType.Landing)
            {
                if (!condition[ConditionFlag.InFlight])
                {
                    Stop();
                }
            }
            else if ((localPlayerPosition - Destination.Position).Length() < Destination.StopDistance)
            {
                if (localPlayerPosition.Y - Destination.Position.Y <= Destination.VerticalStopDistance)
                {
                    Stop();
                }
                else if (Destination.DataId != null)
                {
                    IGameObject? gameObject = gameFunctions.FindObjectByDataId(Destination.DataId.Value);
                    if (gameObject is ICharacter or IEventObj)
                    {
                        if (Math.Abs(localPlayerPosition.Y - gameObject.Position.Y) <
                            DefaultVerticalInteractionDistance)
                        {
                            Stop();
                        }
                    }
                    else if (gameObject is { ObjectKind: ObjectKind.Aetheryte })
                    {
                        if (AetheryteConverter.IsLargeAetheryte((EAetheryteLocation)Destination.DataId))
                        {
                            /*
                            if ((EAetheryteLocation) Destination.DataId is EAetheryteLocation.OldSharlayan
                                or EAetheryteLocation.UltimaThuleAbodeOfTheEa)
                                Stop();

                            // TODO verify the first part of this, is there any aetheryte like that?
                            // TODO Unsure if this is per-aetheryte or what; because e.g. old sharlayan is at -1.53;
                            //      but Elpis aetherytes fail at around -0.95
                            if (localPlayerPosition.Y - gameObject.Position.Y < 2.95f &&
                                localPlayerPosition.Y - gameObject.Position.Y > -0.9f)
                                Stop();
                            */
                            Stop();
                        }
                        else
                        {
                            // aethernet shard
                            if (Math.Abs(localPlayerPosition.Y - gameObject.Position.Y) <
                                DefaultVerticalInteractionDistance)
                            {
                                Stop();
                            }
                        }
                    }
                    else
                    {
                        Stop();
                    }
                }
                else
                {
                    Stop();
                }
            }
            else
            {
                List<Vector3> navPoints = navmeshIpc.GetWaypoints();
                Vector3? start = objectTable[0]?.Position;
                if (start != null)
                {
                    if (Destination.ShouldRecalculateNavmesh() && RecalculateNavmesh(navPoints, start.Value))
                    {
                        return;
                    }

                    if (!Destination.IsFlying && !condition[ConditionFlag.Mounted] &&
                        !gameFunctions.HasStatusPreventingSprint() && Destination.CanSprint)
                    {
                        TriggerSprintIfNeeded(navPoints, start.Value);
                    }
                }
            }
        }
    }

    private void Restart(DestinationData destination)
    {
        Stop();

        if (destination.UseNavmesh)
        {
            NavigateTo(EMovementType.None, destination.DataId, destination.Position, false, false,
                destination.StopDistance, destination.VerticalStopDistance);
        }
        else
        {
            NavigateTo(EMovementType.None, destination.DataId, [destination.Position], false, false,
                destination.StopDistance, destination.VerticalStopDistance);
        }
    }

    private bool IsOnFlightPath(Vector3 p)
    {
        Vector3? pointOnFloor = navmeshIpc.GetPointOnFloor(p, true);
        return pointOnFloor != null && Math.Abs(pointOnFloor.Value.Y - p.Y) > 0.5f;
    }

    [MemberNotNull(nameof(Destination))]
    private void PrepareNavigation(EMovementType type, uint? dataId, Vector3 to, bool fly, bool sprint,
        float? stopDistance, float verticalStopDistance, bool land, bool useNavmesh)
    {
        ResetPathfinding();

        if (InputManager.IsAutoRunning())
        {
            logger.LogInformation("Turning off auto-move");
            chatFunctions.ExecuteCommand("/automove off");
        }

        Destination = new(type, dataId, to, stopDistance ?? (QuestStep.DefaultStopDistance - 0.2f), fly,
            sprint, verticalStopDistance, land, useNavmesh);
        MovementStartedAt = DateTime.MaxValue;
    }

    public void NavigateTo(EMovementType type, uint? dataId, Vector3 to, bool fly, bool sprint,
        float? stopDistance = null, float? verticalStopDistance = null, bool land = false)
    {
        fly |= condition[ConditionFlag.Diving];
        if (fly && land)
        {
            to = to with { Y = to.Y + 2.6f };
        }

        PrepareNavigation(type, dataId, to, fly, sprint, stopDistance, verticalStopDistance ?? DefaultVerticalInteractionDistance, land, true);
        logger.LogInformation("Pathfinding to {Destination}", Destination);

        Destination.NavmeshCalculations++;
        _cancellationTokenSource = new();
        _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));

        Vector3 startPosition = objectTable[0]!.Position;
        if (fly && aetheryteData.CalculateDistance(startPosition, clientState.TerritoryType,
            EAetheryteLocation.CoerthasCentralHighlandsCampDragonhead) < 11f)
        {
            startPosition = startPosition with { Y = startPosition.Y + 1f };
            logger.LogInformation("Using modified start position for flying pathfinding: {StartPosition}",
                startPosition.ToString("G", CultureInfo.InvariantCulture));
        }
        else if (fly)
        {
            // other positions have a (lesser) chance of starting from underground too, in which case pathfinding takes
            // >10 seconds and gets stuck trying to go through the ground.
            // only for flying; as walking uses a different algorithm
            startPosition = startPosition with { Y = startPosition.Y + 0.2f };
        }

        _pathfindTask =
            navmeshIpc.Pathfind(startPosition, to, fly, _cancellationTokenSource.Token);
        _pathfindingSnapshot = true;
        //      float range = stopDistance ?? 2.8f;
        //      if (!_navmeshIpc.SimplePathfindAndMoveCloseTo(to, fly, range))
        //      {
        //          _logger.LogWarning("SimpleMove rejected pathfind request (already in progress), stopping first");
        //          _navmeshIpc.Stop();
        //          if (!_navmeshIpc.SimplePathfindAndMoveCloseTo(to, fly, range))
        //          {
        //              _logger.LogWarning("SimpleMove still rejected after stop");
        //          }
        //      }
        //      MovementStartedAt = DateTime.Now;
    }

    public void NavigateTo(EMovementType type, uint? dataId, List<Vector3> to, bool fly, bool sprint,
        float? stopDistance, float? verticalStopDistance = null, bool land = false)
    {
        fly |= condition[ConditionFlag.Diving];
        if (fly && land && to.Count > 0)
        {
            to[^1] = to[^1] with { Y = to[^1].Y + 2.6f };
        }

        PrepareNavigation(type, dataId, to.Last(), fly, sprint, stopDistance, verticalStopDistance ?? DefaultVerticalInteractionDistance, land, false);

        logger.LogInformation("Moving to {Destination}", Destination);
        navmeshIpc.MoveTo(to, fly);
        _pathRunningSnapshot = true;
        MovementStartedAt = DateTime.Now;
    }

    public void ResetPathfinding()
    {
        if (_cancellationTokenSource != null)
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch(ObjectDisposedException)
            {
            }

            _cancellationTokenSource.Dispose();
        }

        _pathfindTask = null;
        _pathfindingSnapshot = false;
    }

    private bool RecalculateNavmesh(List<Vector3> navPoints, Vector3 start)
    {
        if (Destination == null)
        {
            throw new InvalidOperationException("Destination is null");
        }

        if (DateTime.Now - MovementStartedAt <= TimeSpan.FromSeconds(configuration.General.MovementStuckGraceSeconds))
        {
            return false;
        }

        Vector3 nextWaypoint = navPoints.FirstOrDefault();
        if (nextWaypoint == default)
        {
            return false;
        }

        float distance = Vector2.Distance(new(start.X, start.Z),
            new(nextWaypoint.X, nextWaypoint.Z));
        if (Destination.LastWaypoint == null ||
            (Destination.LastWaypoint.Position - nextWaypoint).Length() > 0.1f)
        {
            Destination.LastWaypoint = new(nextWaypoint)
            {
                Distance2DAtLastUpdate = distance,
                UpdatedAt = Environment.TickCount64
            };
            return false;
        }
        else if (Environment.TickCount64 - Destination.LastWaypoint.UpdatedAt > 500)
        {
            // check whether we've made any progress of any kind
            if (Math.Abs(distance - Destination.LastWaypoint.Distance2DAtLastUpdate) < 0.5f && !condition[ConditionFlag.WatchingCutscene])
            {
                int calculations = Destination.NavmeshCalculations;
                if (calculations % 6 == 1)
                {
                    logger.LogWarning("Jumping to try and resolve navmesh problem (n = {Calculations})",
                        calculations);
                    unsafe
                    {
                        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
                        Destination.NavmeshCalculations++;
                        Destination.LastWaypoint.UpdatedAt = Environment.TickCount64;
                    }
                }
                else
                {
                    logger.LogWarning("Recalculating navmesh (n = {Calculations})", calculations);
                    Restart(Destination);
                }

                Destination.NavmeshCalculations = calculations + 1;
                return true;
            }
            else
            {
                Destination.LastWaypoint.Distance2DAtLastUpdate = distance;
                Destination.LastWaypoint.UpdatedAt = Environment.TickCount64;
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    private void TriggerSprintIfNeeded(IEnumerable<Vector3> navPoints, Vector3 start)
    {
        float actualDistance = 0;
        foreach(Vector3 end in navPoints)
        {
            actualDistance += (start - end).Length();
            start = end;
        }

        unsafe
        {
            // 70 is ~10 seconds of sprint
            float sprintDistance = 100f;

            // if we're in towns/event areas, jog is a neat fallback (if we're not already jogging,
            // if we're too close then sprinting will barely benefit us)
            if (!gameFunctions.HasStatus(EStatus.Jog) &&
                ((int)GameMain.Instance()->CurrentTerritoryIntendedUseId) is 0 or 7 or 13 or 14 or 15 or 19 or 23 or 29)
            {
                sprintDistance = 30f;
            }

            if (actualDistance > sprintDistance &&
                ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 4) == 0)
            {
                logger.LogInformation("Triggering Sprint");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 4);
            }
        }
    }

    /// <summary>停止移動。</summary>
    /// <param name="defer">
    /// 不是 <see langword="null"/> 時，<b>純副作用的那兩段</b>——對 vnavmesh 打 <c>Path.Stop</c>、
    /// 以及關掉自動前進——交給它安排；狀態重設（<see cref="ResetPathfinding"/>、
    /// <see cref="Destination"/>、快照）仍然當場同步做。
    /// </param>
    /// <remarks>
    /// 🔴 <b>為什麼要拆</b>：<c>QuestController.ExecuteNextStep</c> 是持著 <c>_progressLock</c>
    /// 呼叫這一支的，而 <c>navmeshIpc.Stop()</c> 是跨外掛 IPC（CallGate＝直接方法呼叫）
    /// ⇒ 在鎖裡打過去等於把 vnavmesh 的鎖排在我們的鎖後面，對方日後長出任何一條回頭呼叫
    /// Questionable 的路徑就是死鎖。
    /// <para>
    /// 🔴 <b>為什麼不能整支延後</b>：<see cref="ResetPathfinding"/> 與 <c>Destination = null</c>
    /// 會讓「還在不在尋路／移動」翻成 <see langword="false"/>，而<b>同一個鎖裡</b>後面就在判斷
    /// 這兩件事。整支延後的話那兩個判斷會翻面、提早 <c>return</c>——那是縮小引擎的原子性，
    /// 不是等價改寫。
    /// </para>
    /// <para>
    /// ⚠️ <c>InputManager.IsAutoRunning()</c> 讀的是遊戲原生狀態、而且決定要不要做事
    /// ⇒ 那個判斷留在原地，延後的只有「寫一行記錄＋送一個指令」。
    /// </para>
    /// <para>
    /// 📌 <paramref name="defer"/> 不給的時候（UI、指令、<see cref="Dispose"/>、本類別內部的
    /// 十幾個呼叫點）行為逐字不變：就地依序執行。
    /// </para>
    /// </remarks>
    public void Stop(Action<Action>? defer = null)
    {
        if (defer != null)
        {
            defer(navmeshIpc.Stop);
        }
        else
        {
            navmeshIpc.Stop();
        }

        ResetPathfinding();
        Destination = null;

        // 我們已經決定停下來了 ⇒ 快照當場歸零，不必等下一幀重新取樣。
        _pathRunningSnapshot = false;

        if (InputManager.IsAutoRunning())
        {
            if (defer != null)
            {
                defer(TurnOffAutoMove);
            }
            else
            {
                TurnOffAutoMove();
            }
        }
    }

    private void TurnOffAutoMove()
    {
        logger.LogInformation("Turning off auto-move [stop]");
        chatFunctions.ExecuteCommand("/automove off");
    }

    public sealed record DestinationData
    (
        EMovementType MovementType,
        uint? DataId,
        Vector3 Position,
        float StopDistance,
        bool IsFlying,
        bool CanSprint,
        float VerticalStopDistance,
        bool Land,
        bool UseNavmesh)
    {
        public int NavmeshCalculations { get; set; }
        public List<Vector3> PartialRoute { get; } = [];
        public LastWaypointData? LastWaypoint { get; set; }

        public bool ShouldRecalculateNavmesh()
        {
            return NavmeshCalculations < 10;
        }
    }

    public sealed record LastWaypointData(Vector3 Position)
    {
        public long UpdatedAt { get; set; }
        public double Distance2DAtLastUpdate { get; set; }
    }

    public sealed class PathfindingFailedException : Exception
    {
        public PathfindingFailedException()
        {
        }

        public PathfindingFailedException(string message)
            : base(message)
        {
        }

        public PathfindingFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
