using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using SpaceCraft;
using UnityEngine;

namespace SmartDrones
{
    internal static class Patches
    {
        // Direct, fast field access to the private members of LogisticManager.
        private static readonly AccessTools.FieldRef<LogisticManager, Dictionary<int, LogisticTask>> AllTasksRef =
            AccessTools.FieldRefAccess<LogisticManager, Dictionary<int, LogisticTask>>("_allLogisticTasks");

        private static readonly AccessTools.FieldRef<LogisticManager, bool> PausedRef =
            AccessTools.FieldRefAccess<LogisticManager, bool>("_paused");

        // The drone's in-flight task. Read in the DroneUnload prefix BEFORE the method's
        // tail call to EndAssignement() nulls/reassigns it.
        private static readonly AccessTools.FieldRef<Drone, LogisticTask> DroneTaskRef =
            AccessTools.FieldRefAccess<Drone, LogisticTask>("_logisticTask");

        // Postfix on the LogisticTask constructor: stamp each new task with the
        // next monotonic sequence number so we can order oldest-first later.
        internal static void LogisticTaskCtorPostfix(LogisticTask __instance)
        {
            long n = Interlocked.Increment(ref SmartDronesPlugin.Seq);
            SmartDronesPlugin.TaskAge.Add(__instance, new StrongBox<long>(n));
        }

        // Age of a task. If a task was created before our ctor patch was live
        // (should not normally happen - tasks are created at runtime, long after
        // Awake), stamp it lazily so ordering stays deterministic.
        internal static long GetAge(LogisticTask t)
        {
            if (SmartDronesPlugin.TaskAge.TryGetValue(t, out var box))
            {
                return box.Value;
            }
            long n = Interlocked.Increment(ref SmartDronesPlugin.Seq);
            SmartDronesPlugin.TaskAge.Add(t, new StrongBox<long>(n));
            return n;
        }

        // Demand priority of a task = the GetPriority() of the LogisticEntity on the
        // task's demand inventory (verified against decompiled LogisticTask.cs ->
        // demandInventory.GetLogisticEntity() and LogisticEntity.GetPriority()). Higher
        // == more urgent (same convention the stock dispatch uses for group suppression).
        // Any null along the chain (or a throw) -> priority 0, the neutral floor.
        internal static int GetDemandPriority(LogisticTask t)
        {
            try
            {
                var di = t.GetDemandInventory();
                if (di == null) return 0;
                var e = di.GetLogisticEntity();
                if (e == null) return 0;
                return e.GetPriority();
            }
            catch { return 0; }
        }

        // PREFIX replacing LogisticManager.GetANonAttributedTask.
        // Stock behaviour: returns the FIRST NotAttributed task (matching planet)
        // in Dictionary enumeration order - which, because freed buckets get
        // reused by newly created tasks, is effectively LIFO under churn and
        // starves old tasks. We instead pick the OLDEST eligible task (lowest
        // sequence number) => fair FIFO servicing.
        //
        // The requesting drone is NOT passed to this method (callers pass only a
        // planetHash), so a per-drone nearest-supply tie-break is not possible
        // here. v1 is pure oldest-first; this fully orders tasks, so no tie-break
        // is needed for determinism.
        internal static bool GetANonAttributedTaskPrefix(LogisticManager __instance, int planetHash, ref LogisticTask __result)
        {
            if (PausedRef(__instance))
            {
                __result = null;
                return false; // skip original
            }

            var all = AllTasksRef(__instance);

            // Must AGREE with the full-dispatch picker (Dispatch.PickTaskForDrone), or
            // drop-off drones self-serving via this method would grab old low-priority
            // tasks while the main pass hands rockets the fresh high-priority ones.
            // In PriorityThenAge we therefore score identically (no drone position here,
            // so distance can't tie-break; score fully orders anyway, ties -> oldest).
            // Other modes keep the historical pure oldest-first behaviour.
            bool priorityMode = SmartDronesPlugin.AssignMode.Value == AssignmentMode.PriorityThenAge;
            long nowSeq = SmartDronesPlugin.Seq;

            LogisticTask best = null;
            long bestAge = long.MaxValue;   // stamp of chosen (lower == older)
            long bestScore = long.MinValue; // PriorityThenAge only
            int bestPrio = 0;
            long bestAgeTicks = 0;

            foreach (var kv in all)
            {
                LogisticTask task = kv.Value;
                if (task.GetTaskState() != LogisticData.TaskState.NotAttributed)
                {
                    continue;
                }
                if (planetHash != 0 && task.GetPlanetHash() != planetHash)
                {
                    continue;
                }
                long age = GetAge(task);
                if (priorityMode)
                {
                    int prio = GetDemandPriority(task);
                    long ageTicks = nowSeq - age;
                    if (ageTicks < 0) ageTicks = 0;
                    long score = (long)prio * SmartDronesPlugin.PriorityWeight.Value
                                 + ageTicks * SmartDronesPlugin.AgeWeight.Value;
                    // Higher score wins; tie -> older (lower stamp).
                    if (best == null || score > bestScore || (score == bestScore && age < bestAge))
                    {
                        best = task; bestScore = score; bestAge = age;
                        bestPrio = prio; bestAgeTicks = ageTicks;
                    }
                }
                else if (age < bestAge)
                {
                    bestAge = age;
                    best = task;
                }
            }

            __result = best;

            if (SmartDronesPlugin.VerboseAssignmentLog.Value && SmartDronesPlugin.Log != null)
            {
                string grp = "none";
                if (best != null)
                {
                    try { var wo = best.GetWorldObjectToMove(); if (wo != null) grp = SmartDronesPlugin.GroupName(wo.GetGroup()); } catch { }
                }
                string detail = best == null ? "none"
                    : priorityMode
                        ? ("task prio=" + bestPrio + " age=" + bestAgeTicks + " score=" + bestScore + " group=" + grp)
                        : ("task age=" + bestAge + " group=" + grp);
                SmartDronesPlugin.Log.LogDebug(
                    SmartDronesPlugin.TimePrefix() + "GetANonAttributedTask(planet=" + planetHash + ") -> " +
                    detail + " [scanned " + all.Count + " tasks]");
            }

            return false; // skip original
        }

        // PREFIX on Drone.DroneUnload - fires at the moment a drone completes a delivery
        // into its task's demand inventory. At prefix time the drone still holds its
        // _logisticTask (the tail EndAssignement() that nulls it has not run yet), so the
        // destination demand inventory, delivered group, and supply/demand positions are all
        // in scope. Observe-only (void, never skips the original) -> dispatch is unchanged.
        // Emits ONE concise Info line per delivery so a demand inventory's fill curve is a
        // directly readable, timestamped series. Everything is wrapped so it can never throw
        // into the game loop; a null accessor degrades to a minimal line.
        internal static void DroneUnloadPrefix(Drone __instance)
        {
            try
            {
                if (SmartDronesPlugin.DeliveryLog == null || !SmartDronesPlugin.DeliveryLog.Value
                    || SmartDronesPlugin.Log == null)
                {
                    return;
                }

                LogisticTask task = null;
                try { task = DroneTaskRef(__instance); } catch { }
                if (task == null)
                {
                    return;
                }

                Inventory demandInv = task.GetDemandInventory();
                WorldObject wo = task.GetWorldObjectToMove();

                // Mirror DroneUnload's own guard: an item is added only when the drone still
                // holds the world object and the demand inventory exists. If not, nothing is
                // delivered on this call, so don't emit a DELIVERED line.
                Inventory droneInv = null;
                try { droneInv = __instance.GetDroneInventory(); } catch { }
                bool willDeliver = demandInv != null && wo != null
                                   && droneInv != null && droneInv.ContainWorldObject(wo);
                if (!willDeliver)
                {
                    return;
                }

                string group = "null";
                try { group = SmartDronesPlugin.GroupName(wo.GetGroup()); } catch { }

                // Demand inventory id = its container world object id.
                int invId = -1;
                try
                {
                    WorldObject dwo = task.GetDemandInventoryWorldObject();
                    if (dwo != null)
                    {
                        invId = dwo.GetId();
                    }
                    else
                    {
                        LogisticEntity e = demandInv.GetLogisticEntity();
                        if (e != null && e.GetWorldObject() != null)
                        {
                            invId = e.GetWorldObject().GetId();
                        }
                    }
                }
                catch { }

                // Fill AFTER this delivery lands: the live count plus the item about to be
                // added (willDeliver already matched the game's own add condition).
                string fillStr = "?/?";
                try
                {
                    int size = demandInv.GetSize();
                    int fill = demandInv.GetInsideWorldObjects().Count + 1;
                    fillStr = fill + "/" + size;
                }
                catch { }

                // supply -> demand distance (meters) when both positions are known.
                string dist = "n/a";
                try
                {
                    WorldObject supplyWo = task.GetIsSpawnedObject()
                        ? wo
                        : task.GetSupplyInventoryWorldObject();
                    WorldObject demandWo = task.GetDemandInventoryWorldObject();
                    if (TryWoPos(supplyWo, out Vector3 sp) && TryWoPos(demandWo, out Vector3 dp))
                    {
                        dist = Vector3.Distance(sp, dp).ToString("F1") + "m";
                    }
                }
                catch { }

                SmartDronesPlugin.Log.LogInfo(SmartDronesPlugin.TimePrefix()
                    + "DELIVERED group=" + group + " toDemandInv=" + invId
                    + " fill=" + fillStr + " dist=" + dist);
            }
            catch (Exception ex)
            {
                try
                {
                    SmartDronesPlugin.Log?.LogInfo(SmartDronesPlugin.TimePrefix()
                        + "DELIVERED (log-path error: " + ex.Message + ")");
                }
                catch { }
            }
        }

        // World-object position: live GameObject transform, else the stored position, else fail.
        // Never throws.
        private static bool TryWoPos(WorldObject wo, out Vector3 p)
        {
            p = default;
            if (wo == null)
            {
                return false;
            }
            try
            {
                GameObject go = wo.GetGameObject();
                if (go != null)
                {
                    p = go.transform.position;
                    return true;
                }
                Vector3 stored = wo.GetPosition();
                if (stored != Vector3.zero)
                {
                    p = stored;
                    return true;
                }
            }
            catch { }
            return false;
        }

        // PREFIX on LogisticEntity.SanitizeGroups (the SOLE gate stripping any Group
        // present in both the supply and demand lists). Returning false skips the
        // original, preserving dual-role (same group as both supply AND demand)
        // containers. All four setters (Set/Add x Supply/Demand) and the save-load
        // path route through this one method, so this single prefix covers every
        // sanitize call site. When the config is off we return true (run stock).
        internal static bool SanitizeGroupsPrefix()
        {
            return !SmartDronesPlugin.AllowDualRoleGroups.Value;
        }
    }
}
