using System;
using System.Collections.Generic;
using HarmonyLib;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TimedToolReplenish;

public class PluginInfo
{
    public const string PLUGIN_GUID = "nozwock.TimedToolReplenish";
    public const string PLUGIN_NAME = "Timed Tool Replenish";
    public const string PLUGIN_VERSION = "1.0.0";
}

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static Harmony harmony;
    private static ConfigEntry<float> configIdleTime;

    private const string category = "General";

    private void Awake()
    {
        configIdleTime = Config.Bind(category, "Idle Time", 5f, "Try to replenish tools if the player has been idle for this long.");

        harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
    }

    private void OnDestroy()
    {
        harmony.UnpatchSelf();
        harmony = null;
    }

    [HarmonyPatch(typeof(HeroController), "Update")]
    class Patch_ReplenishToolsOnIdle
    {
        static readonly AccessTools.FieldRef<HeroController, GameManager> gmRef =
            AccessTools.FieldRefAccess<HeroController, GameManager>("gm");
        static readonly AccessTools.FieldRef<HeroController, bool> hardLandedRef =
            AccessTools.FieldRefAccess<HeroController, bool>("hardLanded");
        static readonly AccessTools.FieldRef<HeroController, float> attackTimeRef =
            AccessTools.FieldRefAccess<HeroController, float>("attack_time");
        static readonly AccessTools.FieldRef<HeroController, Rigidbody2D> rb2dRef =
            AccessTools.FieldRefAccess<HeroController, Rigidbody2D>("rb2d");
        static readonly System.Func<List<ToolItem>> getCurrentEquippedTools =
            AccessTools.MethodDelegate<System.Func<List<ToolItem>>>(
                AccessTools.Method(typeof(ToolItemManager), "GetCurrentEquippedTools")
            );

        static float idleStateTimer = 0f;

        static void Postfix(HeroController __instance)
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

            // Most of it nabbed from CanPlayNeedolin()
            var isIdle = !hardLanded
                && !gm.isPaused
                && hero_state != GlobalEnums.ActorStates.no_input
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

            if (isIdle)
            {
                idleStateTimer += Time.deltaTime;

                var needsReplenish = false;

                if (idleStateTimer >= configIdleTime.Value)
                {
                    List<ToolItem> currentEquippedTools = getCurrentEquippedTools();
                    foreach (var item in currentEquippedTools)
                    {
                        ToolItemsData.Data toolData = PlayerData.instance.GetToolData(item.name);
                        int toolStorageAmount = ToolItemManager.GetToolStorageAmount(item);

                        if (toolData.AmountLeft < toolStorageAmount)
                        {
                            needsReplenish = true;
                            break;
                        }
                    }

                    if (needsReplenish)
                    {
                        ToolItemManager.TryReplenishTools(true, ToolItemManager.ReplenishMethod.Bench);
                    }

                    // Reset so it won’t spam every frame
                    idleStateTimer = 0f;
                }
            }
            else
            {
                idleStateTimer = 0f;
            }
        }
    }
}
