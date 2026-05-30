using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.Framework;
using System;
using System.Linq;
using static ECommons.GenericHelpers;

namespace Saucy.MiniCactpot;

public unsafe class MiniCactpot : Module
{
    private const int ClickThrottleMs = 600;
    private const uint ConfirmButtonNodeId = 67;

    private const string TalkThrottleKey = "Saucy.MiniCactpot.Talk";

    private readonly int[] scratchState = new int[9];
    private readonly CactpotSolver solver = new();
    private DateTime? cactpotFlowUntilUtc;

    public override string Name => "Mini Cactpot";

    public override void Enable()
    {
        // Defensively unregister first — guards the "had to disable+enable" cold-start case where a previous registration was orphaned.
        Svc.AddonLifecycle.UnregisterListener(OnLotterySetup);
        Svc.AddonLifecycle.UnregisterListener(OnPreFinalize);
        Svc.AddonLifecycle.UnregisterListener(OnSelectYesnoSetup);
        Svc.AddonLifecycle.UnregisterListener(OnTalkUpdate);
        Svc.Framework.Update -= OnFrameworkUpdate;

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryDaily", OnLotterySetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LotteryDaily", OnPreFinalize);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnTalkUpdate);
        Svc.Framework.Update += OnFrameworkUpdate;
        SyncOpenGame();
    }

    public override void Disable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnLotterySetup);
        Svc.AddonLifecycle.UnregisterListener(OnPreFinalize);
        Svc.AddonLifecycle.UnregisterListener(OnSelectYesnoSetup);
        Svc.AddonLifecycle.UnregisterListener(OnTalkUpdate);
        Svc.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnLotterySetup(AddonEvent type, AddonArgs args) =>
        MarkCactpotFlow(TimeSpan.FromMinutes(2));

    // Longer post-game window so Talk dialogue + the next ticket-prompt Yes/No are both still advanced by Saucy.
    private void OnPreFinalize(AddonEvent type, AddonArgs args) =>
        MarkCactpotFlow(TimeSpan.FromSeconds(90));

    private void OnTalkUpdate(AddonEvent type, AddonArgs args) => TryAdvanceDialogue();

    private void OnFrameworkUpdate(IFramework framework)
    {
        TryAdvanceDialogue();

        if (!TryGetLotteryAddon(out var addon))
        {
            return;
        }

        MarkCactpotFlow(TimeSpan.FromMinutes(2));
        ProcessAddon(addon);
    }

    private void SyncOpenGame()
    {
        if (TryGetLotteryAddon(out var addon))
        {
            ProcessAddon(addon);
        }
    }

    private void TryAdvanceDialogue()
    {
        if (!InSaucer || !IsInCactpotFlow())
        {
            return;
        }

        TalkHelper.TryAdvance(TalkThrottleKey);
    }

    private void MarkCactpotFlow(TimeSpan window) =>
        cactpotFlowUntilUtc = DateTime.UtcNow + window;

    private bool IsInCactpotFlow()
    {
        if (TryGetLotteryAddon(out _))
        {
            return true;
        }

        return cactpotFlowUntilUtc != null && DateTime.UtcNow <= cactpotFlowUntilUtc;
    }

    private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
    {
        if (!InSaucer)
        {
            return;
        }

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        if (!EzThrottler.Throttle("MiniCactpotTicketYes", 500))
        {
            return;
        }

        if (!SelectYesnoHelper.PressYes(addon))
        {
            LogVerbose("SelectYesno Yes press failed.");
        }
    }

    private void ProcessAddon(AddonLotteryDaily* addon)
    {
        if (TalkHelper.IsVisible())
        {
            TalkHelper.TryAdvance(TalkThrottleKey);
            return;
        }

        var stage = GetStage(addon);
        ReadBoardState(addon, scratchState);
        var revealed = CountRevealed(scratchState);

        if (stage == 5)
        {
            if (EzThrottler.Throttle("MiniCactpotClose", ClickThrottleMs))
            {
                TryClickConfirmButton(expectedStage: 5);
            }

            return;
        }

        if (revealed < 4)
        {
            if (!EzThrottler.Throttle("MiniCactpotTile", ClickThrottleMs))
            {
                return;
            }

            TryRevealNextTile(addon, scratchState);
            return;
        }

        if (EzThrottler.Throttle("MiniCactpotLane", ClickThrottleMs))
        {
            TrySelectLane(addon, scratchState);
        }

        if (EzThrottler.Throttle("MiniCactpotConfirm", ClickThrottleMs))
        {
            TryClickConfirmButton(expectedStage: -1);
        }
    }

    private void TryRevealNextTile(AddonLotteryDaily* addon, int[] state)
    {
        if (CountRevealed(state) == 0)
        {
            return;
        }

        try
        {
            var solution = solver.Solve(state);
            if (solution.Length == 8)
            {
                return;
            }

            var activeIndexes = CollectActiveIndexes(solution)
                .Where(i => state[i] == 0)
                .ToArray();
            if (activeIndexes.Length == 0)
            {
                return;
            }

            TryClickTile(addon, activeIndexes[0]);
        }
        catch (Exception ex)
        {
            LogError($"Error revealing tile: {ex.Message}");
        }
    }

    private void TrySelectLane(AddonLotteryDaily* addon, int[] state)
    {
        try
        {
            var solution = solver.Solve(state);
            if (solution.Length != 8)
            {
                return;
            }

            var activeIndexes = CollectActiveIndexes(solution);
            if (activeIndexes.Length == 0)
            {
                return;
            }

            var csLane = SolverLaneToCsLane(activeIndexes[0]);
            TryClickLane(addon, csLane);
        }
        catch (Exception ex)
        {
            LogError($"Error selecting lane: {ex.Message}");
        }
    }

    private static bool TryGetLotteryAddon(out AddonLotteryDaily* addon)
    {
        if (TryGetAddonByName("LotteryDaily", out addon) && IsAddonReady((AtkUnitBase*)addon))
        {
            return true;
        }

        addon = null;
        return false;
    }

    private static int GetStage(AddonLotteryDaily* addon)
    {
        var unit = &addon->AtkUnitBase;
        if (unit->AtkValuesCount < 1)
        {
            return -1;
        }

        var value = unit->AtkValues[0];
        return value.Type == AtkValueType.Int ? value.Int : -1;
    }

    private static void ReadBoardState(AddonLotteryDaily* addon, int[] dest)
    {
        for (var i = 0; i < 9; i++)
        {
            dest[i] = addon->GameNumbers[i];
        }
    }

    private static int CountRevealed(int[] state)
    {
        var count = 0;
        for (var i = 0; i < state.Length; i++)
        {
            if (state[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static int[] CollectActiveIndexes(bool[] solution)
    {
        var count = 0;
        for (var i = 0; i < solution.Length; i++)
        {
            if (solution[i])
            {
                count++;
            }
        }

        var activeIndexes = new int[count];
        var index = 0;
        for (var i = 0; i < solution.Length; i++)
        {
            if (solution[i])
            {
                activeIndexes[index++] = i;
            }
        }

        return activeIndexes;
    }

    private static bool TryClickTile(AddonLotteryDaily* addon, int tileIndex)
    {
        var tile = addon->GameBoard[tileIndex];
        if (tile == null)
        {
            return false;
        }

        var button = (AtkComponentButton*)tile;
        if (!button->AtkResNode->IsVisible())
        {
            return false;
        }

        try
        {
            button->ClickAddonButton((AtkUnitBase*)addon);
            addon->AtkUnitBase.Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[MiniCactpot] Tile {tileIndex} click failed");
            return false;
        }
    }

    private static bool TryClickLane(AddonLotteryDaily* addon, int csLane)
    {
        var lane = addon->LaneSelector[csLane];
        if (lane->AtkComponentButton.AtkResNode == null)
        {
            return false;
        }

        try
        {
            lane->ClickRadioButton((AtkUnitBase*)addon);
            addon->AtkUnitBase.Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[MiniCactpot] Lane {csLane} click failed");
            return false;
        }
    }

    private static bool TryClickConfirmButton(int expectedStage)
    {
        if (!TryGetLotteryAddon(out var addon))
        {
            return true;
        }

        var stage = GetStage(addon);
        if (expectedStage == 5)
        {
            if (stage != 5)
            {
                return false;
            }
        }
        else if (stage == 5)
        {
            return true;
        }

        var unit = (AtkUnitBase*)addon;
        var confirmBtn = addon->GetComponentButtonById(ConfirmButtonNodeId);
        if (confirmBtn == null || confirmBtn->AtkResNode == null || !confirmBtn->AtkResNode->IsVisible())
        {
            return false;
        }

        if (!confirmBtn->IsEnabled)
        {
            return false;
        }

        try
        {
            confirmBtn->ClickAddonButton(unit);
            unit->Update(0);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, "[MiniCactpot] Confirm button click failed");
            return false;
        }
    }

    private static int SolverLaneToCsLane(int lane)
        => lane switch
        {
            0 => 5,
            1 => 6,
            2 => 7,
            3 => 1,
            4 => 2,
            5 => 3,
            6 => 0,
            7 => 4,
            var _ => throw new ArgumentOutOfRangeException($"{nameof(lane)}", lane, "Must be between 0 and 8 (inclusive)")
        };
}
