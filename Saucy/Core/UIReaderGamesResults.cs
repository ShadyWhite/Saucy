using FFXIVClientStructs.FFXIV.Component.GUI;
using Saucy.CuffACur;
using System;
using System.Linq;
namespace Saucy;

public class UIReaderGamesResults : IUIReader
{
    private UIStateCuffResults cuffResults = new();
    private UIStateLimbResults limbResults = new();

    private bool needsNotify;
    public Action<UIStateCuffResults>? OnCuffUpdated;
    public Action<UIStateLimbResults>? OnLimbUpdated;

    public bool HasResultsUI { get; private set; }

    public string GetAddonName() => "GoldSaucerReward";

    public void OnAddonLost() => SetIsResultsUI(false);

    public void OnAddonShown(nint addonPtr)
    {
        needsNotify = true;
        cuffResults = new();
        limbResults = new();
    }

    public unsafe void OnAddonUpdate(nint addonPtr)
    {
        var baseNode = (AtkUnitBase*)addonPtr;
        if (baseNode == null || !needsNotify)
        {
            return;
        }

        if (!CufModule.ModuleEnabled && !P.LimbManager.Cfg.EnableLimb)
        {
            needsNotify = false;
            return;
        }

        UpdateCachedState(baseNode);

        if (cuffResults.numMGP >= 0)
        {
            needsNotify = false;
            OnCuffUpdated?.Invoke(cuffResults);
        }

        if (limbResults.numMGP >= 0)
        {
            needsNotify = false;
            OnLimbUpdated?.Invoke(limbResults);
        }
    }

    public void SetIsResultsUI(bool value) => HasResultsUI = value;

    private unsafe void UpdateCachedState(AtkUnitBase* baseNode)
    {
        if (!TryGetRewardMgpTextNode(baseNode, out var number))
        {
            if (CufModule.ModuleEnabled)
            {
                cuffResults.numMGP = -1;
            }

            if (P.LimbManager.Cfg.EnableLimb)
            {
                limbResults.numMGP = -1;
            }

            return;
        }

        if (CufModule.ModuleEnabled)
        {
            if (!int.TryParse(number->NodeText.ToString(), out cuffResults.numMGP))
            {
                cuffResults.numMGP = -1;
            }

            switch (cuffResults.numMGP)
            {
                case 10:
                    cuffResults.isBruising = true; break;
                case 15:
                    cuffResults.isPunishing = true; break;
                case 25:
                    cuffResults.isBrutal = true; break;
            }
        }

        if (P.LimbManager.Cfg.EnableLimb)
        {
            if (!int.TryParse(number->NodeText.ToString().Where(char.IsDigit).ToArray(), out limbResults.numMGP))
            {
                limbResults.numMGP = -1;
            }
        }
    }

    private static unsafe bool TryGetRewardMgpTextNode(AtkUnitBase* baseNode, out AtkTextNode* textNode)
    {
        textNode = null;
        if (baseNode == null)
        {
            return false;
        }

        ref var uld = ref baseNode->UldManager;
        if (uld.NodeListCount <= 4)
        {
            return false;
        }

        var node4 = uld.NodeList[4];
        if (node4 == null)
        {
            return false;
        }

        var component = node4->GetComponent();
        if (component == null)
        {
            return false;
        }

        ref var innerUld = ref component->UldManager;
        if (innerUld.NodeListCount <= 2)
        {
            return false;
        }

        var node2 = innerUld.NodeList[2];
        if (node2 == null)
        {
            return false;
        }

        var innerComponent = node2->GetComponent();
        if (innerComponent == null)
        {
            return false;
        }

        ref var deepestUld = ref innerComponent->UldManager;
        if (deepestUld.NodeListCount <= 1)
        {
            return false;
        }

        var node1 = deepestUld.NodeList[1];
        if (node1 == null)
        {
            return false;
        }

        textNode = node1->GetAsAtkTextNode();
        return textNode != null;
    }
}

public class UIStateCuffResults
{
    public bool isBruising;
    public bool isBrutal;
    public bool isPunishing;
    public int numMGP;
}

public class UIStateLimbResults
{
    public int numMGP;
}
