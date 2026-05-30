using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Saucy.Framework;
using Saucy.IPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetheryteSheet = Lumina.Excel.Sheets.Aetheryte;

namespace Saucy.TripleTriad;

internal enum MultiAreaRouteStepKind
{
    Teleport,
    Aethernet,
    Mount,
    MoveTo,
    Interact,
    SelectYesno,
    WaitForZone
}

internal sealed class MultiAreaRouteStep
{
    public required MultiAreaRouteStepKind Kind { get; init; }
    public uint AetheryteId { get; init; }
    public string? AethernetShardName { get; init; }
    public Vector3 Position { get; init; }
    public bool Fly { get; init; }
    public uint ObjectDataId { get; init; }
    public uint ArrivalObjectDataId { get; init; }
    public uint ApproachMapId { get; init; }
    public float ApproachMapX { get; init; }
    public float ApproachMapY { get; init; }
    public float Range { get; init; } = 6f;
    public bool DismountFirst { get; init; }
}

internal sealed class MultiAreaRoute
{
    public required string Name { get; init; }
    public string? TooltipHint { get; init; }
    public required Func<MapLinkPayload, bool> Matches { get; init; }
    public required IReadOnlyList<MultiAreaRouteStep> Steps { get; init; }
    public IReadOnlyList<uint>? ArrivalTerritoryIds { get; init; }
    public uint InteriorMapId { get; init; }
    public float InteriorMapX { get; init; }
    public float InteriorMapY { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);

    public bool IsInDestinationTerritory(uint territoryId, uint fallbackTargetTerritoryId)
    {
        if (territoryId == fallbackTargetTerritoryId)
        {
            return true;
        }

        if (ArrivalTerritoryIds is not { Count: > 0 })
        {
            return false;
        }

        foreach (var candidate in ArrivalTerritoryIds)
        {
            if (candidate == territoryId)
            {
                return true;
            }
        }

        return false;
    }
}

internal static class MultiAreaRouteRegistry
{
    private static readonly IReadOnlyList<MultiAreaRoute> Routes =
    [
        JeunoFirstWalkRoute.Route,
        FortempsManservantRoute.Route
    ];

    public static MultiAreaRoute? FindRoute(MapLinkPayload location) =>
        Routes.FirstOrDefault(route => route.Matches(location));

    public static bool MatchesDestination(MapLinkPayload location) => FindRoute(location) != null;
}

internal static class AethernetShardLookup
{
    private static readonly Dictionary<string, uint> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static uint Resolve(string shardName)
    {
        if (string.IsNullOrEmpty(shardName))
        {
            return 0;
        }

        if (Cache.TryGetValue(shardName, out var cached))
        {
            return cached;
        }

        var sheet = Svc.Data.GetExcelSheet<AetheryteSheet>();
        if (sheet == null)
        {
            return 0;
        }

        foreach (var row in sheet)
        {
            if (row.IsAetheryte)
            {
                continue;
            }

            var name = row.AethernetName.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.Equals(shardName, StringComparison.OrdinalIgnoreCase))
            {
                Cache[shardName] = row.RowId;
                return row.RowId;
            }
        }

        return 0;
    }
}

internal static unsafe class MultiAreaRouteExecutor
{
    private const uint GeneralActionDismount = 23;
    private const uint GeneralActionFlyingMountRoulette = 24;

    public static bool TryBeginRoute(
        MultiAreaRoute route,
        MultiAreaRouteContext context,
        out RouteExecution execution,
        out string? beginMessage)
    {
        execution = null!;
        beginMessage = null;

        var startIndex = GetFirstIncompleteStepIndex(route, context);
        if (startIndex < 0)
        {
            return false;
        }

        execution = new()
        {
            Route = route, Context = context, StepIndex = startIndex, StepStartedUtc = DateTime.UtcNow
        };

        var step = route.Steps[startIndex];
        if (step.Kind == MultiAreaRouteStepKind.Teleport)
        {
            if (!Lifestream.TryTeleport(step.AetheryteId))
            {
                Svc.Chat.PrintError($"[Saucy] Lifestream could not teleport for {route.Name}.");
                return false;
            }

            beginMessage =
                $"[Saucy] Teleporting for {route.Name}, then moving to the NPC.";
            return true;
        }

        beginMessage =
            $"[Saucy] Entering {route.Name}, then moving to the NPC.";
        return true;
    }

    public static bool Tick(RouteExecution execution)
    {
        if (execution.Failed || execution.StepIndex >= execution.Route.Steps.Count)
        {
            return !execution.Failed && execution.StepIndex >= execution.Route.Steps.Count;
        }

        var step = execution.Route.Steps[execution.StepIndex];
        var complete = step.Kind switch
        {
            MultiAreaRouteStepKind.Teleport => TickTeleport(step),
            MultiAreaRouteStepKind.Aethernet => TickAethernet(step, execution),
            MultiAreaRouteStepKind.Mount => TickMount(),
            MultiAreaRouteStepKind.MoveTo => TickMoveTo(step, execution),
            MultiAreaRouteStepKind.Interact => TickInteract(step),
            MultiAreaRouteStepKind.SelectYesno => TickSelectYesno(step),
            MultiAreaRouteStepKind.WaitForZone => TickWaitForZone(execution),
            var _ => false
        };

        if (execution.Failed)
        {
            return false;
        }

        if (!complete)
        {
            return false;
        }

        execution.StepIndex++;
        execution.StepActionStarted = false;
        execution.StepStartedUtc = DateTime.UtcNow;
        return execution.StepIndex >= execution.Route.Steps.Count;
    }

    public static bool IsTeleportStepComplete(RouteExecution execution, MultiAreaRouteStep teleportStep)
    {
        if (execution.StepIndex != 0 || teleportStep.Kind != MultiAreaRouteStepKind.Teleport)
        {
            return false;
        }

        return TickTeleport(teleportStep);
    }

    private static int GetFirstIncompleteStepIndex(MultiAreaRoute route, MultiAreaRouteContext context)
    {
        for (var i = 0; i < route.Steps.Count; i++)
        {
            var step = route.Steps[i];
            if (step.Kind == MultiAreaRouteStepKind.Teleport &&
                Svc.ClientState.TerritoryType == GetAetheryteTerritoryId(step.AetheryteId))
            {
                continue;
            }

            if (step.Kind == MultiAreaRouteStepKind.WaitForZone &&
                route.IsInDestinationTerritory(Svc.ClientState.TerritoryType, context.TargetTerritoryId))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool TickTeleport(MultiAreaRouteStep step)
    {
        if (Lifestream.IsBusyNow())
        {
            return false;
        }

        if (!Player.Interactable || IsBetweenAreas() || Player.IsAnimationLocked)
        {
            return false;
        }

        var territoryId = GetAetheryteTerritoryId(step.AetheryteId);
        return territoryId != 0 && Svc.ClientState.TerritoryType == territoryId;
    }

    private static bool TickAethernet(MultiAreaRouteStep step, RouteExecution execution)
    {
        var shardId = ResolveShardId(step);
        if (shardId == 0)
        {
            Svc.Chat.PrintError($"[Saucy] Could not resolve aethernet shard \"{step.AethernetShardName}\".");
            execution.Failed = true;
            return false;
        }

        if (!execution.StepActionStarted)
        {
            // Already at the shard? Treat as complete so we don't bounce the player on partial runs.
            var shardPos = AetheryteHelper.GetAethernetShardWorldPosition(shardId);
            if (shardPos != null && Vector3.Distance(Player.Position, shardPos.Value) <= 10f)
            {
                return true;
            }

            if (Lifestream.IsBusyNow() || !Player.Interactable || IsBetweenAreas() || Player.IsAnimationLocked)
            {
                return false;
            }

            if (!Lifestream.TryAethernetTeleportById(shardId))
            {
                Svc.Chat.PrintError($"[Saucy] Lifestream could not aethernet for {execution.Route.Name}.");
                execution.Failed = true;
                return false;
            }

            execution.StepActionStarted = true;
            execution.StepStartedUtc = DateTime.UtcNow;
            return false;
        }

        if (Lifestream.IsBusyNow() || IsBetweenAreas() || !Player.Interactable || Player.IsAnimationLocked)
        {
            return false;
        }

        // Brief settle after aethernet before the next step.
        return DateTime.UtcNow - execution.StepStartedUtc > TimeSpan.FromSeconds(2);
    }

    private static uint ResolveShardId(MultiAreaRouteStep step)
    {
        if (!string.IsNullOrEmpty(step.AethernetShardName))
        {
            return AethernetShardLookup.Resolve(step.AethernetShardName);
        }

        return step.AetheryteId;
    }

    private static bool TickSelectYesno(MultiAreaRouteStep step)
    {
        if (!SelectYesnoHelper.TryGetVisible(out var yesno))
        {
            return false;
        }

        if (SelectYesnoHelper.IsBlockedSystemPrompt(yesno))
        {
            return false;
        }

        if (!EzThrottler.Throttle("SaucyRouteYesno"))
        {
            return false;
        }

        if (!SelectYesnoHelper.PressYes(yesno))
        {
            return false;
        }

        return true;
    }

    private static bool TickMount()
    {
        if (!Player.Interactable || IsBetweenAreas())
        {
            return false;
        }

        return TryMountUp();
    }

    private static bool TickMoveTo(MultiAreaRouteStep step, RouteExecution execution)
    {
        if (!execution.StepActionStarted)
        {
            if (!Player.Interactable || IsBetweenAreas())
            {
                return false;
            }

            if (!Vnavmesh.IsNavReady())
            {
                Svc.Chat.PrintError("[Saucy] vnavmesh is not ready for this route step.");
                execution.Failed = true;
                return false;
            }

            var approachPoint = ResolveApproachPoint(step);
            approachPoint = Vnavmesh.TryGetPointOnFloor(approachPoint) ?? approachPoint;
            if (!Vnavmesh.TryPathfindAndMoveTo(approachPoint, step.Fly))
            {
                Svc.Chat.PrintError("[Saucy] vnavmesh could not start movement for this route step.");
                execution.Failed = true;
                return false;
            }

            execution.StepActionStarted = true;
            return false;
        }

        if (Vnavmesh.IsPathfindInProgress() || Vnavmesh.IsPathRunning())
        {
            return false;
        }

        if (!Player.Interactable || Player.IsAnimationLocked)
        {
            return false;
        }

        var resolvedPoint = ResolveApproachPoint(step);
        var pointOnFloor = Vnavmesh.TryGetPointOnFloor(resolvedPoint) ?? resolvedPoint;
        return IsMoveToComplete(step, pointOnFloor);
    }

    private static Vector3 ResolveApproachPoint(MultiAreaRouteStep step)
    {
        if (step.ArrivalObjectDataId != 0)
        {
            var target = FindObject(step.ArrivalObjectDataId);
            if (target != null)
            {
                return target.Position;
            }
        }

        if (step.ApproachMapId != 0)
        {
            var fromMap = TriadMapNavigation.GetWorldPositionFromMap(
                step.ApproachMapId, step.ApproachMapX, step.ApproachMapY);
            if (fromMap != null)
            {
                return fromMap.Value;
            }
        }

        return step.Position;
    }

    private static bool IsMoveToComplete(MultiAreaRouteStep step, Vector3 approachPoint)
    {
        if (step.ArrivalObjectDataId != 0)
        {
            var target = FindObject(step.ArrivalObjectDataId);
            if (target != null && Vector3.Distance(Player.Position, target.Position) <= step.Range)
            {
                return true;
            }
        }

        return Vector3.Distance(Player.Position, approachPoint) <= 8f;
    }

    private static bool TickInteract(MultiAreaRouteStep step)
    {
        if (step.DismountFirst && !TryDismount())
        {
            return false;
        }

        var target = FindObject(step.ObjectDataId);
        if (target == null)
        {
            return false;
        }

        if (Vector3.Distance(Player.Position, target.Position) > step.Range)
        {
            return false;
        }

        if (Svc.Targets.Target?.BaseId != step.ObjectDataId)
        {
            Svc.Targets.Target = target;
            return false;
        }

        if (!EzThrottler.Throttle("SaucyRouteInteract"))
        {
            return false;
        }

        TargetSystem.Instance()->InteractWithObject((GameObject*)target.Address, false);
        return true;
    }

    private static bool TickWaitForZone(RouteExecution execution)
    {
        if (!Player.Interactable || Lifestream.IsBusyNow())
        {
            return false;
        }

        if (execution.Route.IsInDestinationTerritory(
            Svc.ClientState.TerritoryType, execution.Context.TargetTerritoryId))
        {
            return true;
        }

        if (IsBetweenAreas())
        {
            return false;
        }

        if (SelectYesnoHelper.TryGetVisible(out var yesno) &&
            !SelectYesnoHelper.IsBlockedSystemPrompt(yesno) &&
            EzThrottler.Throttle("SaucyRouteYesno"))
        {
            SelectYesnoHelper.PressYes(yesno);
            return false;
        }

        if (TalkHelper.TryAdvance("SaucyRouteTalk"))
        {
            return false;
        }

        if (DateTime.UtcNow - execution.StepStartedUtc > TimeSpan.FromSeconds(30))
        {
            Svc.Chat.PrintError($"[Saucy] Did not arrive in {execution.Route.Name} after zone transition.");
            execution.Failed = true;
        }

        return false;
    }

    private static bool TryMountUp()
    {
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition] || Svc.Condition[ConditionFlag.Casting])
        {
            EzThrottler.Throttle("SaucyRouteMountWait", 2000, true);
        }

        if (!EzThrottler.Check("SaucyRouteMountWait"))
        {
            return false;
        }

        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, GeneralActionFlyingMountRoulette) != 0)
        {
            return true;
        }

        if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyRouteMount"))
        {
            return false;
        }

        Chat.ExecuteGeneralAction(GeneralActionFlyingMountRoulette);
        return false;
    }

    private static bool TryDismount()
    {
        if (!Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (Player.IsAnimationLocked || !EzThrottler.Throttle("SaucyRouteDismount"))
        {
            return false;
        }

        Chat.ExecuteGeneralAction(GeneralActionDismount);
        return false;
    }

    private static IGameObject? FindObject(uint dataId) =>
        Svc.Objects
            .Where(o => o.IsTargetable && o.BaseId == dataId)
            .OrderBy(o => Vector3.Distance(Player.Position, o.Position))
            .FirstOrDefault();

    private static uint GetAetheryteTerritoryId(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<AetheryteSheet>()?.GetRowOrDefault(aetheryteId);
        return row?.Territory.RowId ?? 0;
    }

    private static bool IsBetweenAreas() => Svc.Condition[ConditionFlag.BetweenAreas];

    internal sealed class RouteExecution
    {
        public required MultiAreaRouteContext Context;
        public bool Failed;
        public required MultiAreaRoute Route;
        public bool StepActionStarted;
        public int StepIndex;
        public DateTime StepStartedUtc;
    }

    internal sealed class MultiAreaRouteContext
    {
        public required Vector3 Destination;
        public required MapLinkPayload Location;
        public required uint TargetTerritoryId;
    }
}

internal static class FortempsManservantRoute
{
    private const uint FoundationAetheryteId = 70;
    private const uint LastVigilAethernetShardId = 87;
    private const uint GatekeeperNpcDataId = 1011217;
    private const uint FortempsManorTerritoryId = 433;
    private const uint PillarsMapId = 219;
    private const uint ManorInteriorMapId = 222;
    private const float GuardMapX = 11.5f;
    private const float GuardMapY = 11.0f;
    private const float ManservantMapX = 6f;
    private const float ManservantMapY = 6f;
    private static readonly uint[] FortempsManorTerritoryIds = [FortempsManorTerritoryId];

    internal static readonly MultiAreaRoute Route = new()
    {
        Name = "Fortemps Manor",
        TooltipHint = "Foundation aetheryte, aethernet to The Last Vigil, then enter the manor.",
        ArrivalTerritoryIds = FortempsManorTerritoryIds,
        InteriorMapId = ManorInteriorMapId,
        InteriorMapX = ManservantMapX,
        InteriorMapY = ManservantMapY,
        Matches = location =>
        {
            if (location.TerritoryType.RowId == FortempsManorTerritoryId)
            {
                return true;
            }

            var placeName = location.PlaceName.ToString();
            return placeName.Contains("Fortemps Manor", StringComparison.OrdinalIgnoreCase);
        },
        Timeout = TimeSpan.FromSeconds(240),
        Steps =
        [
            new()
            {
                Kind = MultiAreaRouteStepKind.Teleport, AetheryteId = FoundationAetheryteId
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.Aethernet, AetheryteId = LastVigilAethernetShardId
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.MoveTo,
                ApproachMapId = PillarsMapId,
                ApproachMapX = GuardMapX,
                ApproachMapY = GuardMapY,
                Fly = false,
                ArrivalObjectDataId = GatekeeperNpcDataId,
                Range = 6f
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.Interact, ObjectDataId = GatekeeperNpcDataId, Range = 6f, DismountFirst = true
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.WaitForZone
            }
        ]
    };
}

internal static class JeunoFirstWalkRoute
{
    private const uint MamookAetheryteId = 206;
    private const uint EntranceDataId = 2014450;
    private static readonly Vector3 YakTelPortalApproachPoint = new(-527.2f, -152.4f, 668.5f);
    private static readonly uint[] LowerJeunoTerritoryIds = [1265, 1190, 1191, 1192];

    internal static readonly MultiAreaRoute Route = new()
    {
        Name = "Jeuno: The First Walk",
        TooltipHint = "Lower Jeuno routes via Mamook and the Yak T'el portal.",
        ArrivalTerritoryIds = LowerJeunoTerritoryIds,
        Matches = location =>
        {
            var placeName = location.PlaceName.ToString();
            return placeName.Contains("Lower Jeuno", StringComparison.OrdinalIgnoreCase) ||
                   placeName.Contains("Jeuno: The First Walk", StringComparison.OrdinalIgnoreCase);
        },
        Timeout = TimeSpan.FromSeconds(180),
        Steps =
        [
            new()
            {
                Kind = MultiAreaRouteStepKind.Teleport, AetheryteId = MamookAetheryteId
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.Mount
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.MoveTo, Position = YakTelPortalApproachPoint, Fly = true, ArrivalObjectDataId = EntranceDataId
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.Interact, ObjectDataId = EntranceDataId, Range = 6f
            },
            new()
            {
                Kind = MultiAreaRouteStepKind.WaitForZone
            }
        ]
    };
}
