using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoBlight
{
    // ==================== SETTINGS ====================

    public class AutoBlightSettings : ModSettings
    {
        public static bool enabled = true;
        public static bool homeZoneOnly = false;
        public static bool showNotification = true;
        public static bool boostPriority = true;
        public static bool blockSowing = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref homeZoneOnly, "homeZoneOnly", false);
            Scribe_Values.Look(ref showNotification, "showNotification", true);
            Scribe_Values.Look(ref boostPriority, "boostPriority", true);
            Scribe_Values.Look(ref blockSowing, "blockSowing", true);
            base.ExposeData();
        }

        public static void DrawSettings(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            l.CheckboxLabeled("AB_Enabled".Translate(), ref enabled, "AB_Enabled_Desc".Translate());
            l.CheckboxLabeled("AB_HomeZoneOnly".Translate(), ref homeZoneOnly, "AB_HomeZoneOnly_Desc".Translate());
            l.CheckboxLabeled("AB_ShowNotification".Translate(), ref showNotification);
            l.CheckboxLabeled("AB_BoostPriority".Translate(), ref boostPriority, "AB_BoostPriority_Desc".Translate());
            l.CheckboxLabeled("AB_BlockSowing".Translate(), ref blockSowing, "AB_BlockSowing_Desc".Translate());
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

        // Track active blight state
        public bool blightActive;
        private int blightStartTick = -1;

        // Track growing zones that had blight (to block replanting)
        private HashSet<int> blightedZoneCells = new HashSet<int>();

        // Saved original plant cut priorities per pawn (to restore later)
        private bool prioritiesBoosted;

        public MapComponent_AutoBlight(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (!AutoBlightSettings.enabled) return;
            // Scan every 120 ticks (~2 seconds) for fast response
            if (Find.TickManager.TicksGame % 120 != 0) return;

            int count = 0;
            bool anyBlightOnMap = false;
            List<Thing> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);

            for (int i = 0; i < plants.Count; i++)
            {
                Plant plant = plants[i] as Plant;
                if (plant == null) continue;
                if (!plant.Blighted) continue;

                anyBlightOnMap = true;

                if (alreadyDesignated.Contains(plant.thingIDNumber)) continue;

                // Home zone check
                if (AutoBlightSettings.homeZoneOnly && !map.areaManager.Home[plant.Position])
                    continue;

                // Track the cell as blighted (for sow blocking)
                if (AutoBlightSettings.blockSowing)
                    blightedZoneCells.Add(map.cellIndices.CellToIndex(plant.Position));

                // Check if already designated for cut
                if (map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null)
                {
                    alreadyDesignated.Add(plant.thingIDNumber);
                    continue;
                }
                if (map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null)
                {
                    // Switch harvest designation to cut -- blight makes harvest useless
                    map.designationManager.RemoveAllDesignationsOn(plant);
                    map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                    alreadyDesignated.Add(plant.thingIDNumber);
                    count++;
                    continue;
                }

                // Designate for cutting
                map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                alreadyDesignated.Add(plant.thingIDNumber);
                count++;
            }

            // Blight state transitions
            if (anyBlightOnMap && !blightActive)
            {
                // Blight just started
                blightActive = true;
                blightStartTick = Find.TickManager.TicksGame;

                // Boost plant cut priority for all colonists
                if (AutoBlightSettings.boostPriority)
                    BoostCutPlantPriority();
            }
            else if (!anyBlightOnMap && blightActive)
            {
                // Blight is over
                blightActive = false;
                blightedZoneCells.Clear();

                // Restore original priorities
                if (AutoBlightSettings.boostPriority && prioritiesBoosted)
                    RestoreCutPlantPriority();
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

        /// <summary>
        /// Temporarily set PlantCutting to priority 1 for all capable colonists.
        /// </summary>
        private void BoostCutPlantPriority()
        {
            WorkTypeDef cutPlants = DefDatabase<WorkTypeDef>.GetNamedSilentFail("PlantCutting");
            if (cutPlants == null) return;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.workSettings == null) continue;
                if (pawn.WorkTypeIsDisabled(cutPlants)) continue;

                int current = pawn.workSettings.GetPriority(cutPlants);
                // Only boost if not already high priority and not disabled (0)
                if (current > 1 || current == 0)
                {
                    pawn.workSettings.SetPriority(cutPlants, 1);
                }
            }
            prioritiesBoosted = true;
            Log.Message("[AutoBlight] Blight detected! PlantCutting priority boosted to 1 for all colonists.");
        }

        /// <summary>
        /// Restore PlantCutting to priority 3 (normal) after blight is cleared.
        /// </summary>
        private void RestoreCutPlantPriority()
        {
            WorkTypeDef cutPlants = DefDatabase<WorkTypeDef>.GetNamedSilentFail("PlantCutting");
            if (cutPlants == null) return;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.workSettings == null) continue;
                if (pawn.WorkTypeIsDisabled(cutPlants)) continue;

                int current = pawn.workSettings.GetPriority(cutPlants);
                // Only lower if we boosted it (it's at 1)
                if (current == 1)
                {
                    pawn.workSettings.SetPriority(cutPlants, 3);
                }
            }
            prioritiesBoosted = false;
            Log.Message("[AutoBlight] Blight cleared. PlantCutting priority restored to normal.");
        }

        /// <summary>
        /// Check if a cell is in an active blight zone (for sow blocking).
        /// </summary>
        public bool IsCellBlighted(IntVec3 cell)
        {
            if (!blightActive || !AutoBlightSettings.blockSowing) return false;
            return blightedZoneCells.Contains(map.cellIndices.CellToIndex(cell));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref alreadyDesignated, "ab_designated", LookMode.Value);
            Scribe_Values.Look(ref blightActive, "ab_blightActive", false);
            Scribe_Values.Look(ref prioritiesBoosted, "ab_prioritiesBoosted", false);
            if (alreadyDesignated == null) alreadyDesignated = new HashSet<int>();
            if (blightedZoneCells == null) blightedZoneCells = new HashSet<int>();
        }
    }

    /// <summary>
    /// During active blight, BLOCK cutting non-blighted plants (trees, stumps).
    /// Only blighted plants can be cut. This forces pawns to deal with blight first.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_PlantsCut), nameof(WorkGiver_PlantsCut.HasJobOnThing))]
    public static class Patch_BlockNonBlightCut
    {
        public static void Postfix(WorkGiver_PlantsCut __instance, Pawn pawn, Thing t,
            ref bool __result)
        {
            try
            {
                if (!__result) return; // Already no job, skip
                if (!AutoBlightSettings.enabled || !AutoBlightSettings.boostPriority) return;
                if (pawn?.Map == null) return;

                var comp = pawn.Map.GetComponent<MapComponent_AutoBlight>();
                if (comp == null || !comp.blightActive) return;

                // If this plant is NOT blighted, block the job
                Plant plant = t as Plant;
                if (plant == null || !plant.Blighted)
                {
                    __result = false; // No job on non-blighted plants during blight
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// During active blight, also block harvesting. All plant work should focus on blight.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnThing))]
    public static class Patch_BlockHarvestDuringBlight
    {
        public static void Postfix(Pawn pawn, Thing t, ref bool __result)
        {
            try
            {
                if (!__result) return;
                if (!AutoBlightSettings.enabled || !AutoBlightSettings.boostPriority) return;
                if (pawn?.Map == null) return;

                var comp = pawn.Map.GetComponent<MapComponent_AutoBlight>();
                if (comp == null || !comp.blightActive) return;

                // Block ALL harvesting while blight is active -- focus on cutting blight
                __result = false;
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Block sowing in blighted zones during active blight.
    /// Prevents pawns from planting new crops where blight will just spread to them.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell))]
    public static class Patch_BlockSowing
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref Job __result)
        {
            try
            {
                if (!AutoBlightSettings.enabled || !AutoBlightSettings.blockSowing) return;
                if (__result == null || pawn?.Map == null) return;

                var comp = pawn.Map.GetComponent<MapComponent_AutoBlight>();
                if (comp == null || !comp.blightActive) return;

                if (comp.IsCellBlighted(c))
                {
                    __result = null; // Block the sowing job
                }
            }
            catch (Exception) { }
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
