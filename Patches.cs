using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using SpaceCraft;

namespace SmartDrones
{
    internal static class Patches
    {
        // Direct, fast field access to the private members of LogisticManager.
        private static readonly AccessTools.FieldRef<LogisticManager, Dictionary<int, LogisticTask>> AllTasksRef =
            AccessTools.FieldRefAccess<LogisticManager, Dictionary<int, LogisticTask>>("_allLogisticTasks");

        private static readonly AccessTools.FieldRef<LogisticManager, bool> PausedRef =
            AccessTools.FieldRefAccess<LogisticManager, bool>("_paused");

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
