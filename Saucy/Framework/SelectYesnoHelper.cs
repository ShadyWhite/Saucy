using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static ECommons.GenericHelpers;

namespace Saucy.Framework;

public static unsafe class SelectYesnoHelper
{
    private static DateTime? armedUntilUtc;

    public static void ArmForYes(TimeSpan window) => armedUntilUtc = DateTime.UtcNow + window;

    public static void Disarm() => armedUntilUtc = null;

    public static bool IsArmed => armedUntilUtc != null && DateTime.UtcNow < armedUntilUtc;

    public static bool TryGetVisible(out AddonSelectYesno* yesno)
    {
        yesno = null;
        for (var i = 1; i < 100; i++)
        {
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
            if (addon == null)
            {
                return false;
            }

            if (!IsAddonReady(addon))
            {
                continue;
            }

            yesno = (AddonSelectYesno*)addon;
            return true;
        }

        return false;
    }

    public static bool PressYes(AddonSelectYesno* yesno = null)
    {
        if (!TryResolve(yesno, out yesno))
        {
            return false;
        }

        return PressCallback(yesno, 0, static master => master.Yes());
    }

    public static bool PressNo(AddonSelectYesno* yesno = null)
    {
        if (!TryResolve(yesno, out yesno))
        {
            return false;
        }

        return PressCallback(yesno, 1, static master => master.No());
    }

    public static bool TryPressArmedYes()
    {
        if (!IsArmed || !TryGetVisible(out var yesno))
        {
            return false;
        }

        if (!PressYes(yesno))
        {
            return false;
        }

        Disarm();
        return true;
    }

    private static bool TryResolve(AddonSelectYesno* yesno, out AddonSelectYesno* resolved)
    {
        if (yesno != null && IsAddonReady(&yesno->AtkUnitBase))
        {
            resolved = yesno;
            return true;
        }

        return TryGetVisible(out resolved);
    }

    private static bool PressCallback(AddonSelectYesno* yesno, int callbackId, Action<AddonMaster.SelectYesno> fallback)
    {
        try
        {
            yesno->FireCallbackInt(callbackId);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] FireCallbackInt({callbackId}) failed; falling back to AddonMaster");
        }

        try
        {
            fallback(new AddonMaster.SelectYesno(yesno));
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose(ex, $"[SelectYesno] AddonMaster fallback failed for callback {callbackId}");
            return false;
        }
    }
}
