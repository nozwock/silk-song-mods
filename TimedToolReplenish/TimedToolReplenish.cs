using System;
using System.Collections.Generic;
using HarmonyLib;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using UnityEngine;
using GlobalSettings;
using GlobalEnums;

namespace TimedToolReplenish;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public const string PLUGIN_GUID = "nozwock.TimedToolReplenish";
    public const string PLUGIN_NAME = "Timed Tool Replenish";
    public const string PLUGIN_VERSION = "1.3.0";

    internal static ManualLogSource Log;

    static Harmony harmony;

    static ConfigEntry<ReplenishMode> configReplenishMode;
    static ConfigEntry<float> configIdleTime;
    static ConfigEntry<float> configGradualReplenishTime;
    static ConfigEntry<bool> configReplenishBlueTools;
    static ConfigEntry<int> configGradualReplenishPercentage;
    static ConfigEntry<bool> configInfiniteLiquidRefill;
    static ConfigEntry<float> configToolCurrencyCostMultiplier;

    const string GENERAL = "General";
    const string IDLE_MODE = "Idle Replenish Mode";
    const string GRADUAL_MODE = "Gradual Replenish Mode";

    private void Awake()
    {
        Log = base.Logger;

        configReplenishMode = Config.Bind(GENERAL, "Replenish Mode", ReplenishMode.Idle);
        configReplenishBlueTools = Config.Bind(GENERAL, "Replenish Blue Tools", false);
        configIdleTime = Config.Bind(IDLE_MODE, "Idle Time", 5f, "Time the player must remain idle before tools begin to replenish.");
        configGradualReplenishTime = Config.Bind(GRADUAL_MODE, "Replenish Waiting Time", 10f, "Restores a portion of tool resources at regular intervals (in seconds).");
        configGradualReplenishPercentage = Config.Bind(GRADUAL_MODE, "Replenish Percentage", 10, "Percentage of tool resources restored at each interval.");
        configInfiniteLiquidRefill = Config.Bind(GENERAL, "Infinite Liquid Tool Reserve", false);
        configToolCurrencyCostMultiplier = Config.Bind(GENERAL, "Tool Currency Cost Multiplier", 1f, "Adjusts the currency cost (Shards/Rosary) needed to restore tools, using this multiplier.");

        harmony = new Harmony(PLUGIN_GUID);
        harmony.PatchAll();

        foreach (var m in harmony.GetPatchedMethods())
        {
            Log.LogInfo($"Patched: {m.DeclaringType.FullName}.{m.Name}");
        }
    }

    private void OnDestroy()
    {
        harmony.UnpatchSelf();
        harmony = null;
    }

    enum ReplenishMode
    {
        None,
        Idle,
        Gradual
    }

    [HarmonyPatch(typeof(ToolItem))]
    class Patch_ToolItem
    {
        [HarmonyPatch(nameof(ToolItem.ReplenishUsageMultiplier), MethodType.Getter)]
        [HarmonyPostfix]
        static void ReplenishUsageMultiplier_Postfix(ref float __result)
        {
            __result *= configToolCurrencyCostMultiplier.Value;
        }
    }

    [HarmonyPatch(typeof(ToolItemStatesLiquid))]
    class Patch_ToolItemStatesLiquid
    {
        [HarmonyPatch(nameof(ToolItemStatesLiquid.HasInfiniteRefills), MethodType.Getter)]
        [HarmonyPrefix]
        static bool HasInfiniteRefills_Prefix(ref bool __result)
        {
            if (configInfiniteLiquidRefill.Value)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPatch(nameof(ToolItemStatesLiquid.LiquidSavedData), MethodType.Getter)]
        [HarmonyPostfix]
        static void LiquidSavedData_Postfix(ref ToolItemStatesLiquid __instance, ref ToolItemLiquidsData.Data __result)
        {
            if (configInfiniteLiquidRefill.Value)
            {
                __result.RefillsLeft = __instance.RefillsMax;
            }
        }
    }

    [HarmonyPatch(typeof(HeroController))]
    class Patch_HeroController
    {
        static readonly AccessTools.FieldRef<HeroController, GameManager> gmRef =
            AccessTools.FieldRefAccess<HeroController, GameManager>("gm");
        static readonly AccessTools.FieldRef<HeroController, bool> hardLandedRef =
            AccessTools.FieldRefAccess<HeroController, bool>("hardLanded");
        static readonly AccessTools.FieldRef<HeroController, float> attackTimeRef =
            AccessTools.FieldRefAccess<HeroController, float>("attack_time");
        static readonly AccessTools.FieldRef<HeroController, Rigidbody2D> rb2dRef =
            AccessTools.FieldRefAccess<HeroController, Rigidbody2D>("rb2d");

        static float gradualModeTimer = 0f;
        static float idleStateTimer = 0f;

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        static void Update_Postfix(HeroController __instance)
        {
            var gm = gmRef(__instance);
            var cState = __instance.cState;
            var playerData = __instance.playerData;
            var controlReqlinquished = __instance.controlReqlinquished;
            var hero_state = __instance.hero_state;
            var hardLanded = hardLandedRef(__instance);
            var attack_time = attackTimeRef(__instance);
            var Config = __instance.Config;
            var rb2d = rb2dRef(__instance);

            if (configReplenishMode.Value == ReplenishMode.Gradual)
            {
                if (
                    gm.isPaused
                    || hero_state == ActorStates.no_input
                    || cState.transitioning
                    // Messes up the UI if you try to replenish tool when just loading into the game, this prevents that.
                    || controlReqlinquished
                    || playerData.atBench
                )
                {
                    return;
                }

                gradualModeTimer += Time.deltaTime;

                if (gradualModeTimer < configGradualReplenishTime.Value)
                {
                    return;
                }

                gradualModeTimer = 0f;

                ToolReplenishUtil.TryReplenishTools(true, ToolItemManager.ReplenishMethod.BenchSilent);
            }
            else if (configReplenishMode.Value == ReplenishMode.Idle)
            {
                // Most of it nabbed from CanPlayNeedolin()
                var isIdle = !hardLanded
                    && !gm.isPaused
                    && hero_state != ActorStates.no_input
                    && !cState.dashing
                    && !cState.isToolThrowing
                    && (!cState.attacking || !(attack_time < Config.AttackRecoveryTime))
                    && ((!controlReqlinquished && cState.onGround) || playerData.atBench)
                    && !cState.hazardDeath
                    && rb2d.linearVelocity.y > -0.1f
                    && !cState.hazardRespawning
                    && !cState.recoilFrozen
                    && !cState.recoiling
                    && !cState.transitioning;

                if (!isIdle || playerData.atBench)
                {
                    if (!gm.isPaused) // Pause shouldn't reset the timer
                    {
                        idleStateTimer = 0f;
                    }
                    return;
                }

                idleStateTimer += Time.deltaTime;

                if (idleStateTimer < configIdleTime.Value)
                {
                    return;
                }

                // Reset so it won’t spam every frame
                idleStateTimer = 0f;

                ToolReplenishUtil.TryReplenishTools(true, ToolItemManager.ReplenishMethod.Bench);
            }
        }
    }

    class ToolReplenishUtil
    {
        internal static readonly System.Func<List<ToolItem>> getCurrentEquippedTools =
            AccessTools.MethodDelegate<System.Func<List<ToolItem>>>(
                AccessTools.Method(typeof(ToolItemManager), "GetCurrentEquippedTools")
            );

        static float[] _startingCurrencyAmounts;
        static float[] _endingCurrencyAmounts;
        static Dictionary<ToolItemStatesLiquid, int> _liquidCostsTemp;
        static Dictionary<ToolItem, (int, int)> _maxReplenishAmount = new();

        static void ComputeMaxReplenishAmount(List<ToolItem> currentEquippedTools)
        {
            _maxReplenishAmount.Clear();
            foreach (var item in currentEquippedTools)
            {
                var toolData = PlayerData.instance.GetToolData(item.name);
                int toolStorageAmount = ToolItemManager.GetToolStorageAmount(item);
                _maxReplenishAmount[item] = (toolData.AmountLeft,
                    Math.Max(1, (int)Math.Round(toolStorageAmount * (double)Math.Clamp(configGradualReplenishPercentage.Value, 0, 100) / 100, 0)));
            }
        }
        static bool ReachedMaxRefill(ToolItem item, ToolItemsData.Data toolData)
        {
            if (!_maxReplenishAmount.TryGetValue(item, out var entry))
                return false; // fallback

            var (og, max) = entry;
            return (toolData.AmountLeft - og) >= max;
        }

        internal static bool TryReplenishTools(bool doReplenish, ToolItemManager.ReplenishMethod method)
        {
            if (string.IsNullOrEmpty(PlayerData.instance.CurrentCrestID))
            {
                return false;
            }
            bool flag = false; // didReplenish
            _ = HeroController.instance;
            List<ToolItem> currentEquippedTools = getCurrentEquippedTools();
            if (currentEquippedTools == null)
            {
                return false;
            }
            ArrayForEnumAttribute.EnsureArraySize(ref _startingCurrencyAmounts, typeof(CurrencyType));
            ArrayForEnumAttribute.EnsureArraySize(ref _endingCurrencyAmounts, typeof(CurrencyType));
            Array values = Enum.GetValues(typeof(CurrencyType));
            for (int i = 0; i < values.Length; i++)
            {
                CurrencyType type = (CurrencyType)values.GetValue(i);
                _endingCurrencyAmounts[i] = (_startingCurrencyAmounts[i] = CurrencyManager.GetCurrencyAmount(type));
            }
            _liquidCostsTemp?.Clear();
            bool flag2 = false; // tryReplenish
            bool flag3 = true;
            currentEquippedTools.RemoveAll(
                (ToolItem tool) => tool == null
                || !tool.IsAutoReplenished()
                || (!configReplenishBlueTools.Value && tool.Type == ToolItemType.Blue)
            );

            if (configReplenishMode.Value == ReplenishMode.Gradual)
            {
                ComputeMaxReplenishAmount(currentEquippedTools);
            }

            while (flag3) // needReplenish
            {
                flag3 = false;
                foreach (ToolItem item in currentEquippedTools)
                {
                    if ((method == ToolItemManager.ReplenishMethod.QuickCraft && item.ReplenishResource == ToolItem.ReplenishResources.None) || item.ReplenishUsage == ToolItem.ReplenishUsages.OneForOne)
                    {
                        continue;
                    }
                    ToolItemsData.Data toolData = PlayerData.instance.GetToolData(item.name);
                    int toolStorageAmount = ToolItemManager.GetToolStorageAmount(item);
                    if (toolData.AmountLeft >= toolStorageAmount)
                    {
                        continue;
                    }

                    if (configReplenishMode.Value == ReplenishMode.Gradual && ReachedMaxRefill(item, toolData))
                    {
                        continue;
                    }

                    flag2 = true;

                    float outCost = item.ReplenishUsage switch
                    {
                        ToolItem.ReplenishUsages.Percentage => 1f / (float)item.BaseStorageAmount * (float)Gameplay.ToolReplenishCost,
                        ToolItem.ReplenishUsages.OneForOne => 1f,
                        ToolItem.ReplenishUsages.Custom => 0f,
                        _ => throw new ArgumentOutOfRangeException(),
                    } * item.ReplenishUsageMultiplier;
                    float inCost = outCost;
                    int reserveCost;
                    // isReplenishOk
                    bool flag4 = item.TryReplenishSingle(doReplenish: false, outCost, out outCost, out reserveCost);
                    if (flag4 && doReplenish)
                    {
                        if (item.ReplenishResource != ToolItem.ReplenishResources.None && _endingCurrencyAmounts[(int)item.ReplenishResource] - outCost <= -0.5f)
                        {
                            continue;
                        }
                        reserveCost = 0;
                        flag4 = item.TryReplenishSingle(doReplenish: true, inCost, out outCost, out reserveCost);
                    }
                    // filledLiquidOrItem
                    bool flag5 = true;
                    if (item is ToolItemStatesLiquid toolItemStatesLiquid)
                    {
                        if (_liquidCostsTemp == null)
                        {
                            _liquidCostsTemp = new Dictionary<ToolItemStatesLiquid, int>();
                        }
                        int valueOrDefault = _liquidCostsTemp.GetValueOrDefault(toolItemStatesLiquid, 0);
                        if (toolItemStatesLiquid.LiquidSavedData.RefillsLeft > valueOrDefault && !toolItemStatesLiquid.LiquidSavedData.UsedExtra)
                        {
                            valueOrDefault += reserveCost;
                            _liquidCostsTemp[toolItemStatesLiquid] = valueOrDefault;
                        }
                        else
                        {
                            flag5 = false;
                        }
                    }
                    if (!flag4)
                    {
                        continue;
                    }
                    if (outCost > 0f && item.ReplenishResource != ToolItem.ReplenishResources.None)
                    {
                        float num = _endingCurrencyAmounts[(int)item.ReplenishResource];
                        if (num <= 0f || num - outCost <= -0.5f)
                        {
                            continue;
                        }
                        num -= outCost;
                        _endingCurrencyAmounts[(int)item.ReplenishResource] = Mathf.Max(num, 0f);
                    }
                    if (doReplenish && flag5)
                    {
                        toolData.AmountLeft++;
                        if (toolData.AmountLeft < toolStorageAmount)
                        {
                            flag3 = true;
                        }
                        PlayerData.instance.Tools.SetData(item.name, toolData);
                    }
                    flag = true;
                }
            }
            if (!flag && method == ToolItemManager.ReplenishMethod.QuickCraft && Mathf.CeilToInt(_endingCurrencyAmounts[1]) > 0)
            {
                flag = true;
                if (flag2)
                {
                    _endingCurrencyAmounts[1] = 0f;
                }
                else
                {
                    float num2 = _endingCurrencyAmounts[1];
                    num2 -= (float)Gameplay.ToolmasterQuickCraftNoneUsage;
                    if (num2 < 0f)
                    {
                        num2 = 0f;
                    }
                    _endingCurrencyAmounts[1] = num2;
                }
            }
            if (!doReplenish)
            {
                return flag;
            }
            bool flag6 = method != ToolItemManager.ReplenishMethod.BenchSilent;
            for (int num3 = 0; num3 < values.Length; num3++)
            {
                int num4 = Mathf.RoundToInt(_startingCurrencyAmounts[num3] - _endingCurrencyAmounts[num3]);
                if (num4 > 0)
                {
                    CurrencyManager.TakeCurrency(num4, (CurrencyType)values.GetValue(num3), flag6);
                }
            }
            if (_liquidCostsTemp != null)
            {
                foreach (var (toolItemStatesLiquid3, num6) in _liquidCostsTemp)
                {
                    if (num6 <= 0)
                    {
                        if (flag6)
                        {
                            toolItemStatesLiquid3.ShowLiquidInfiniteRefills();
                        }
                    }
                    else
                    {
                        toolItemStatesLiquid3.TakeLiquid(num6, flag6);
                    }
                }
                _liquidCostsTemp.Clear();
            }
            ToolItemManager.ReportAllBoundAttackToolsUpdated();
            ToolItemManager.SendEquippedChangedEvent(force: true);
            return flag;
        }
    }
}
