using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

namespace SandbagSpawner
{
    [StaticConstructorOnStartup]
    public static class Patch_SandbagLogic_Burst
    {
        private static readonly Dictionary<int, int> cooldownCache = new Dictionary<int, int>();
        private static readonly Dictionary<Thing, int> cleanupQueue = new Dictionary<Thing, int>();
        private static readonly List<Thing> removalList = new List<Thing>();
        private const int CleanupInterval = 600;

        static Patch_SandbagLogic_Burst()
        {
            var harmony = new Harmony("TSA.CE.RaidersPlaceSandbags");

            // Hooks the vanilla bridge between Warmup and Projectiles.
            // This is "invisible" to Aimbot's CombatExtended-specific patches.
            var targetMethod = AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");

            if (targetMethod != null)
            {
                harmony.Patch(targetMethod, postfix: new HarmonyMethod(typeof(Patch_SandbagLogic_Burst), nameof(BurstShotPostfix)));
            }

            harmony.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapPostTick)),
                postfix: new HarmonyMethod(typeof(Patch_SandbagLogic_Burst), nameof(OptimizedTickCleanup)));

            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.DeinitAndRemoveMap)),
                postfix: new HarmonyMethod(typeof(Patch_SandbagLogic_Burst), nameof(ClearCachesForMap)));
        }

        /// <summary>
        /// Prune queue for the map being removed only — do not wipe other maps' sandbags.
        /// Clearing the whole cooldown cache is fine (soft timing).
        /// </summary>
        public static void ClearCachesForMap(Map map)
        {
            removalList.Clear();
            foreach (var kvp in cleanupQueue)
            {
                var t = kvp.Key;
                if (t == null || !t.Spawned || t.Map == map)
                    removalList.Add(t);
            }
            foreach (var item in removalList)
                cleanupQueue.Remove(item);

            cooldownCache.Clear();
            removalList.Clear();
        }

        public static void BurstShotPostfix(Verb __instance)
        {
            Pawn pawn = __instance.CasterPawn;

            // NPC factions only (never player). Hostile always; Neutral/Allied via settings.
            if (pawn == null || pawn.Faction == null || !pawn.Spawned) return;
            if (pawn.Faction.IsPlayer) return;
            if (!IsAllowedFactionRelation(pawn.Faction)) return;

            int ticks = Find.TickManager.TicksGame;
            int cooldownTicks = Mathf.RoundToInt(SandbagMod.Settings.CooldownSeconds * 60f);

            if (cooldownCache.TryGetValue(pawn.thingIDNumber, out int lastTick) && ticks < lastTick + cooldownTicks)
                return;

            if (!IsAllowedWeapon(__instance.EquipmentSource)) return;

            if (!__instance.CurrentTarget.IsValid) return;

            if (SpawnSandbags(pawn, __instance.CurrentTarget.Cell))
            {
                cooldownCache[pawn.thingIDNumber] = ticks;
            }
        }

        private static bool IsAllowedFactionRelation(Faction faction)
        {
            var settings = SandbagMod.Settings;
            switch (faction.RelationKindWith(Faction.OfPlayer))
            {
                case FactionRelationKind.Hostile:
                    return true;
                case FactionRelationKind.Neutral:
                    return settings == null || settings.ApplyToNeutral;
                case FactionRelationKind.Ally:
                    return settings == null || settings.ApplyToAllied;
                default:
                    return false;
            }
        }

        private static bool IsAllowedWeapon(ThingWithComps weapon)
        {
            return WeaponMatchHelper.IsAllowedWeapon(weapon, SandbagMod.Settings?.Filters);
        }

        private static bool SpawnSandbags(Pawn pawn, IntVec3 targetCell)
        {
            Map map = pawn.Map;
            IntVec3 shooterPos = pawn.Position;
            Vector3 dirVec = (targetCell - shooterPos).ToVector3();

            if (dirVec.sqrMagnitude < 2.25f) return false;

            dirVec.Normalize();
            IntVec3 forward = new IntVec3(Mathf.RoundToInt(dirVec.x), 0, Mathf.RoundToInt(dirVec.z));
            IntVec3 sideOffset = new IntVec3(-forward.z, 0, forward.x);
            IntVec3[] positions = { shooterPos + forward, shooterPos + forward + sideOffset, shooterPos + forward - sideOffset };

            bool spawnedAtLeastOne = false;
            int deathTick = Find.TickManager.TicksGame + Mathf.RoundToInt(SandbagMod.Settings.LifespanSeconds * 60f);

            foreach (var pos in positions)
            {
                if (IsValidCell(pos, map))
                {
                    Thing sandbag = ThingMaker.MakeThing(ThingDefOf.Sandbags, ThingDefOf.Cloth);
                    GenSpawn.Spawn(sandbag, pos, map);
                    sandbag.SetFaction(pawn.Faction);
                    cleanupQueue[sandbag] = deathTick;
                    FleckMaker.ThrowDustPuff(pos.ToVector3Shifted(), map, 1.2f);
                    spawnedAtLeastOne = true;
                }
            }
            return spawnedAtLeastOne;
        }

        private static bool IsValidCell(IntVec3 pos, Map map)
        {
            if (!pos.InBounds(map)) return false;
            if (pos.GetEdifice(map) != null || pos.GetFirstItem(map) != null) return false;
            if (map.thingGrid.ThingsListAtFast(pos).Any(t => t is Pawn)) return false;

            TerrainDef terrain = pos.GetTerrain(map);
            return ThingDefOf.Sandbags.terrainAffordanceNeeded == null ||
                   (terrain.affordances != null && terrain.affordances.Contains(ThingDefOf.Sandbags.terrainAffordanceNeeded));
        }

        public static void OptimizedTickCleanup(Map __instance)
        {
            if (Find.TickManager.TicksGame % CleanupInterval != 0 || cleanupQueue.Count == 0) return;
            int currentTick = Find.TickManager.TicksGame;
            removalList.Clear();

            foreach (var kvp in cleanupQueue)
            {
                var t = kvp.Key;
                if (t == null || !t.Spawned)
                {
                    removalList.Add(t);
                    continue;
                }

                if (currentTick < kvp.Value)
                    continue;

                // Only destroy/dequeue for this map; leave other maps' expired entries alone.
                if (t.Map != __instance)
                    continue;

                t.Destroy(DestroyMode.Vanish);
                removalList.Add(t);
            }

            foreach (var item in removalList)
                cleanupQueue.Remove(item);
        }
    }
}
