using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoBlight
{
    // ==================== SETTINGS ====================

    public class AutoBlightSettings : ModSettings
    {
        public static bool enabled = true;
        public static bool homeZoneOnly = false;
        public static bool showNotification = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref homeZoneOnly, "homeZoneOnly", false);
            Scribe_Values.Look(ref showNotification, "showNotification", true);
            base.ExposeData();
        }

        public static void DrawSettings(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            l.CheckboxLabeled("AB_Enabled".Translate(), ref enabled, "AB_Enabled_Desc".Translate());
            l.CheckboxLabeled("AB_HomeZoneOnly".Translate(), ref homeZoneOnly, "AB_HomeZoneOnly_Desc".Translate());
            l.CheckboxLabeled("AB_ShowNotification".Translate(), ref showNotification);
            l.End();
        }
    }

    public class AutoBlightMod : Mod
    {
        public static AutoBlightSettings settings;

        public AutoBlightMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<AutoBlightSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            AutoBlightSettings.DrawSettings(inRect);
        }

        public override string SettingsCategory() => "LxO - Auto Cut Blight";
    }

    // ==================== HARMONY ====================

    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("Lexxers.AutoBlight");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[AutoBlight] Initialized.");
        }
    }

    /// <summary>
    /// MapComponent that scans for blighted plants periodically.
    /// More reliable than trying to patch the exact moment blight spawns.
    /// </summary>
    public class MapComponent_AutoBlight : MapComponent
    {
        private HashSet<int> alreadyDesignated = new HashSet<int>();

        public MapComponent_AutoBlight(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (!AutoBlightSettings.enabled) return;
            // Scan every 120 ticks (~2 seconds) for fast response
            if (Find.TickManager.TicksGame % 120 != 0) return;

            int count = 0;
            List<Thing> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);

            for (int i = 0; i < plants.Count; i++)
            {
                Plant plant = plants[i] as Plant;
                if (plant == null) continue;
                if (!plant.Blighted) continue;
                if (alreadyDesignated.Contains(plant.thingIDNumber)) continue;

                // Home zone check
                if (AutoBlightSettings.homeZoneOnly && !map.areaManager.Home[plant.Position])
                    continue;

                // Check if already designated for cut
                if (map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null)
                {
                    alreadyDesignated.Add(plant.thingIDNumber);
                    continue;
                }
                if (map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null)
                {
                    alreadyDesignated.Add(plant.thingIDNumber);
                    continue;
                }

                // Designate for cutting
                map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                alreadyDesignated.Add(plant.thingIDNumber);
                count++;
            }

            if (count > 0 && AutoBlightSettings.showNotification)
            {
                Messages.Message("AB_BlightDetected".Translate(count),
                    MessageTypeDefOf.NegativeEvent, false);
            }

            // Cleanup tracking every 2500 ticks
            if (Find.TickManager.TicksGame % 2500 == 0)
            {
                alreadyDesignated.RemoveWhere(id =>
                {
                    for (int i = 0; i < plants.Count; i++)
                    {
                        if (plants[i].thingIDNumber == id) return false;
                    }
                    return true;
                });
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref alreadyDesignated, "ab_designated", LookMode.Value);
            if (alreadyDesignated == null) alreadyDesignated = new HashSet<int>();
        }
    }

    /// <summary>
    /// Add MapComponent to new/loaded maps.
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Patch_MapInit
    {
        public static void Postfix(Map __instance)
        {
            if (__instance.GetComponent<MapComponent_AutoBlight>() == null)
                __instance.components.Add(new MapComponent_AutoBlight(__instance));
        }
    }
}
