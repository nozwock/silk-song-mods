using System;
using HarmonyLib;
using BepInEx;
using BepInEx.Configuration;

namespace AllDropRosary;

public class PluginInfo
{
    public const string PLUGIN_GUID = "nozwock.AllEnemiesDropRosary";
    public const string PLUGIN_NAME = "All Enemies Drop Rosary";
    public const string PLUGIN_VERSION = "1.0.1";
}

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static Harmony harmony;
    private static ConfigEntry<int> configMaxGeo;
    private static ConfigEntry<int> configHpForHalfGeo;

    private const string category = "General";

    private void Awake()
    {
        configMaxGeo = Config.Bind(category, "Max Rosary", 40, "The maximum Rosary an enemy can drop that doesn't drop it in vanilla.");
        configHpForHalfGeo = Config.Bind(category, "HP for Half of Max Rosary", 250, "The HP at which half of the Max Rosary should be given.\nSmaller value will reach the max cap sooner.");

        harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
    }

    private void OnDestroy()
    {
        harmony.UnpatchSelf();
        harmony = null;
    }

    [HarmonyPatch]
    public class PatchExtraGeo
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealthManager), "SpawnCurrency")]
        public static void ExtraGeo(
            ref int smallGeoCount,
            ref int mediumGeoCount,
            ref int largeGeoCount,
            ref int largeSmoothGeoCount,
            int ___initHp)
        {
            if (
                smallGeoCount == 0
                && mediumGeoCount == 0
                && largeGeoCount == 0
                && largeSmoothGeoCount == 0
            )
            {
                smallGeoCount = ScaleGeo(___initHp);
            }
        }
    }

    private static int ScaleGeo(int hp)
    {
        var scaledGeo = configMaxGeo.Value * (1 - Math.Exp(-hp / configHpForHalfGeo.Value));
        return Math.Max(1, (int)Math.Round(scaledGeo, 0));
    }
}
