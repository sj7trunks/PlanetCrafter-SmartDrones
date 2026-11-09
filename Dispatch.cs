using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using SpaceCraft;
using UnityEngine;
using UnityEngine.Jobs;

namespace SmartDrones
{
    public enum AssignmentMode
    {
        // Oldest-first across all NotAttributed tasks on the drone's planet.
        FIFO,
        // Nearest supply to the drone wins, ignoring age.
        NearestFirst,
        // Oldest-first, but tasks within NearestTieBreakAgeWindow of the oldest
        // are tie-broken by nearest supply to the drone.
        FifoWithNearestTieBreak,
        // (default) Highest score wins, score = demandPriority*PriorityWeight +
        // ageTicks*AgeWeight. Higher-priority demands are served first, but a
        // task's age (ticks = tasks created since it was queued) grows without
        // bound, so ANY waiting task eventually outscores any fixed priority gap
        // -> no starvation. Distance to the drone's supply is the tie-break.
        //
        // Crossover math (defaults W=PriorityWeight=1000, A=AgeWeight=1):
        //   a priority-0 task beats a FRESH priority-P competitor once its
        //   ageTicks > P*W  (e.g. P=1 => 1000 task-creations). "ageTicks" counts
        //   LogisticTask constructions since this task was stamped, so the wall-
        //   clock wait is  P*W / (task creation rate). At the observed churn
        //   (a shuttle fill alone = 25 tasks, plus steady background hauls, call
        //   it ~20-100 tasks/s under load) one priority level of starvation clears
        //   in ~10-50 s; even a slow 5 tasks/s clears in ~200 s. Bounded in
        //   minutes, not hours, for any realistic priority gap.
        PriorityThenAge
    }

    // Full reimplementation of LogisticManager.SetLogisticTasks, ported faithfully
    // from the decompiled coroutine (LogisticManager.cs L429-641) with four behavior
    // changes. See SmartDronesPlugin notes for the change list.
    internal static class Dispatch
    {
        // ---- Private-member access into LogisticManager -------------------------
        private static readonly AccessTools.FieldRef<LogisticManager, bool> UpdatingRef =
            AccessTools.FieldRefAccess<LogisticManager, bool>("_updatingLogisticTasks");
        private static readonly AccessTools.FieldRef<LogisticManager, bool> PausedRef =
            AccessTools.FieldRefAccess<LogisticManager, bool>("_paused");
        private static readonly AccessTools.FieldRef<LogisticManager, List<Inventory>> DemandInvRef =
            AccessTools.FieldRefAccess<LogisticManager, List<Inventory>>("_demandInventories");
        private static readonly AccessTools.FieldRef<LogisticManager, List<Inventory>> SupplyInvRef =
            AccessTools.FieldRefAccess<LogisticManager, List<Inventory>>("_supplyInventories");
        private static readonly AccessTools.FieldRef<LogisticManager, Dictionary<int, Dictionary<int, int>>> TaskPrioRef =
            AccessTools.FieldRefAccess<LogisticManager, Dictionary<int, Dictionary<int, int>>>("_taskPriorities");
        private static readonly AccessTools.FieldRef<LogisticManager, Dictionary<int, LogisticTask>> AllTasksRef =
            AccessTools.FieldRefAccess<LogisticManager, Dictionary<int, LogisticTask>>("_allLogisticTasks");
        private static readonly AccessTools.FieldRef<LogisticManager, List<int>> KeysToDestroyRef =
            AccessTools.FieldRefAccess<LogisticManager, List<int>>("_keysToDestroyBuffer");
        private static readonly AccessTools.FieldRef<LogisticManager, List<Group>> CurrentDemandGroupsRef =
            AccessTools.FieldRefAccess<LogisticManager, List<Group>>("_currentDemandGroups");
        private static readonly AccessTools.FieldRef<LogisticManager, HashSet<MachineDroneStation>> AllStationsRef =
            AccessTools.FieldRefAccess<LogisticManager, HashSet<MachineDroneStation>>("_allDroneStations");
        private static readonly AccessTools.FieldRef<LogisticManager, List<LogisticStationDistanceToTask>> StationsDistRef =
            AccessTools.FieldRefAccess<LogisticManager, List<LogisticStationDistanceToTask>>("_stationsDistancesBuffer");
        private static readonly AccessTools.FieldRef<LogisticManager, TransformAccessArray> AccessArrayRef =
            AccessTools.FieldRefAccess<LogisticManager, TransformAccessArray>("_accessArray");
        private static readonly AccessTools.FieldRef<LogisticManager, List<Drone>> AccessDronesRef =
            AccessTools.FieldRefAccess<LogisticManager, List<Drone>>("_accessArrayCorrespondingDrones");

        private delegate void VoidSelf(LogisticManager self);
        private delegate LogisticTask CreateTaskDel(LogisticManager self, Inventory supply, Inventory demand, WorldObject wo);
        private delegate LogisticTask CreateSpawnedDel(LogisticManager self, Inventory demand, WorldObject wo);

        private static readonly VoidSelf CompleteJob =
            AccessTools.MethodDelegate<VoidSelf>(AccessTools.Method(typeof(LogisticManager), "CompleteDroneTransformUpdateJob"));
        private static readonly CreateTaskDel CreateTask =
            AccessTools.MethodDelegate<CreateTaskDel>(AccessTools.Method(typeof(LogisticManager), "CreateNewTaskForWorldObject"));
        private static readonly CreateSpawnedDel CreateSpawned =
            AccessTools.MethodDelegate<CreateSpawnedDel>(AccessTools.Method(typeof(LogisticManager), "CreateNewTaskForWorldObjectForSpawnedObject"));

        // ---- Fault handling -----------------------------------------------------
        private const int FailureThreshold = 3;
        private static int _consecutiveFailures;
        private static bool _loggedFailureThisStreak;

        // Reusable buffers (main-thread only; never allocate per-pass).
        private static readonly List<SupplyCand> _candBuf = new List<SupplyCand>();
        private static readonly List<TaskCand> _taskBuf = new List<TaskCand>();

        private struct SupplyCand { public Inventory Inv; public float Dist; public int Order; }
        private struct TaskCand { public LogisticTask Task; public long Age; public int Prio; public long AgeTicks; public long Score; }

        // ---- Harmony prefix on LogisticManager.SetLogisticTasks -----------------
        // Returns false (skip original) and substitutes our IEnumerator when the
        // full-dispatch replacement is enabled; otherwise returns true (run stock).
        internal static bool SetLogisticTasksPrefix(LogisticManager __instance, ref IEnumerator __result)
        {
            if (!SmartDronesPlugin.FullDispatchReplacement.Value)
            {
                return true; // let the stock coroutine run (v0.1 fallback path)
            }
            __result = Run(__instance);
            return false;
        }

        // Wrapper enumerator: runs RunCore but guards every segment between yields
        // with try/catch (C# forbids `yield` inside a try-with-catch, so the catch
        // wraps MoveNext and the yield happens outside it). On failure it logs once,
        // resets the guard so dispatch is never lost, and after repeated failures
        // auto-disables the replacement (the 1 Hz driver then runs the stock pass).
        private static IEnumerator Run(LogisticManager mgr)
        {
            IEnumerator inner = RunCore(mgr);
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext())
                    {
                        _consecutiveFailures = 0;
                        _loggedFailureThisStreak = false;
                        yield break;
                    }
                    cur = inner.Current;
                }
                catch (Exception e)
                {
                    HandleFailure(mgr, e);
                    yield break;
                }
                yield return cur;
            }
        }

        private static void HandleFailure(LogisticManager mgr, Exception e)
        {
            // Never leave the guard stuck true, or the 1 Hz driver stops dispatching.
            try { UpdatingRef(mgr) = false; } catch { /* ignore */ }

            _consecutiveFailures++;
            if (!_loggedFailureThisStreak)
            {
                _loggedFailureThisStreak = true;
                SmartDronesPlugin.Log?.LogError(
                    "SmartDrones dispatch replacement threw (failure " + _consecutiveFailures +
                    "/" + FailureThreshold + "): " + e);
            }
            else
            {
                SmartDronesPlugin.Log?.LogDebug("SmartDrones dispatch failure " + _consecutiveFailures +
                    "/" + FailureThreshold + ": " + e.Message);
            }

            if (_consecutiveFailures >= FailureThreshold)
            {
                SmartDronesPlugin.FullDispatchReplacement.Value = false;
                SmartDronesPlugin.Log?.LogError(
                    "SmartDrones: auto-disabling FullDispatchReplacement after " + FailureThreshold +
                    " consecutive failures. Reverting to stock dispatch (v0.1 oldest-first prefix still active). "
                    + "Set Patches.FullDispatchReplacement=true in config to re-enable after investigating.");
            }
        }

        // ---- Faithful port of SetLogisticTasks ----------------------------------
        private static IEnumerator RunCore(LogisticManager mgr)
        {
            UpdatingRef(mgr) = true;

            var demandInventories = DemandInvRef(mgr);
            var supplyInventories = SupplyInvRef(mgr);
            var taskPriorities = TaskPrioRef(mgr);
            var allLogisticTasks = AllTasksRef(mgr);
            var currentDemandGroups = CurrentDemandGroupsRef(mgr);

            HashSet<WorldObject> allPickableWorldObject = WorldObjectsHandler.Instance.GetPickablesByDronesWorldObjects();
            int loopFrameLimit = 400;
            int num = 0;
            CompleteJob(mgr);
            foreach (var tp in taskPriorities)
            {
                tp.Value.Clear();
            }

            // --- Phase 1: create supply->demand tasks ---------------------------
            int j = 0;
            while (j < demandInventories.Count)
            {
                Inventory demandInventory = demandInventories[j];
                int availableDemandSlots = Mathf.Max(0,
                    demandInventory.GetSize()
                    - demandInventory.GetInsideWorldObjects().Count
                    - demandInventory.GetLogisticEntity().waitingDemandSlots);

                if (!demandInventory.IsFull() && availableDemandSlots != 0
                    && demandInventory.GetLogisticEntity().GetWorldObject() != null
                    && taskPriorities.ContainsKey(demandInventory.GetLogisticEntity().GetPlanetHash()))
                {
                    int demandPlanet = demandInventory.GetLogisticEntity().GetPlanetHash();
                    var demandPrio = taskPriorities[demandPlanet];

                    currentDemandGroups.Clear();
                    foreach (Group g in demandInventory.GetLogisticEntity().GetDemandGroups())
                    {
                        currentDemandGroups.Add(g);
                    }

                    // Position of the demand inventory (for CHANGE 1 nearest-supply binding).
                    bool haveDemandPos = TryInvPos(demandInventory, out Vector3 demandPos);

                    foreach (Group demandedGroup in currentDemandGroups)
                    {
                        // _taskPriorities group suppression: a higher-priority inventory
                        // already claimed this group this pass -> skip.
                        if (demandPrio.TryGetValue(demandedGroup.stableHashCode, out var claimedPrio)
                            && demandInventory.GetLogisticEntity().GetPriority() < claimedPrio)
                        {
                            continue;
                        }

                        // CHANGE 1: build the candidate supply list (same filter as stock's
                        // supply loop), order by distance from the demand inventory, nearest
                        // first. Falls back to stock list order when positions are unavailable.
                        _candBuf.Clear();
                        for (int k = 0; k < supplyInventories.Count; k++)
                        {
                            Inventory inv = supplyInventories[k];
                            if (inv != demandInventory
                                && inv.GetLogisticEntity().GetWorldObject() != null
                                && inv.GetLogisticEntity().GetPlanetHash() == demandPlanet
                                && inv.GetLogisticEntity().GetSupplyGroups().Contains(demandedGroup))
                            {
                                // v0.3 anti-ping-pong: when dual-role is allowed, a candidate
                                // supply that ALSO demands this same group at a priority >= the
                                // demanding inventory's priority would form (or invert) a drain
                                // cycle. Skip it. This still permits a strictly-lower-priority
                                // buffer (LOW demand + supply G) to feed a HIGH-demand consumer,
                                // while structurally blocking equal/inverted cycles between two
                                // dual-role containers.
                                if (SmartDronesPlugin.AllowDualRoleGroups.Value)
                                {
                                    LogisticEntity candEnt = inv.GetLogisticEntity();
                                    if (candEnt.GetDemandGroups().Contains(demandedGroup)
                                        && candEnt.GetPriority() >= demandInventory.GetLogisticEntity().GetPriority())
                                    {
                                        if (SmartDronesPlugin.VerboseAssignmentLog.Value)
                                        {
                                            SmartDronesPlugin.Log?.LogDebug(
                                                SmartDronesPlugin.TimePrefix() + "skip dual-role candidate (anti-pingpong) group="
                                                + SmartDronesPlugin.GroupName(demandedGroup)
                                                + " candPrio=" + candEnt.GetPriority()
                                                + " demandPrio=" + demandInventory.GetLogisticEntity().GetPriority());
                                        }
                                        continue;
                                    }
                                }
                                float dist = float.MaxValue;
                                if (haveDemandPos && TryInvPos(inv, out Vector3 sp))
                                {
                                    dist = Vector3.Distance(demandPos, sp);
                                }
                                _candBuf.Add(new SupplyCand { Inv = inv, Dist = dist, Order = k });
                            }
                        }
                        if (haveDemandPos)
                        {
                            _candBuf.Sort(CompareSupply);
                        }
                        // else: leave in insertion (stock list) order.

                        for (int ci = 0; ci < _candBuf.Count; ci++)
                        {
                            Inventory inventory = _candBuf[ci].Inv;
                            var inside = inventory.GetInsideWorldObjects();
                            for (int l = 0; l < inside.Count; l++)
                            {
                                WorldObject worldObject = inside[l];
                                if (worldObject.GetGroup() == demandedGroup
                                    && CreateTask(mgr, inventory, demandInventory, worldObject) != null)
                                {
                                    if (SmartDronesPlugin.VerboseAssignmentLog.Value)
                                    {
                                        SmartDronesPlugin.Log?.LogDebug(
                                            SmartDronesPlugin.TimePrefix() + "bind supply->demand group=" + SmartDronesPlugin.GroupName(demandedGroup) +
                                            " dist=" + _candBuf[ci].Dist.ToString("F1") +
                                            " (cand " + (ci + 1) + "/" + _candBuf.Count + ")");
                                    }
                                    availableDemandSlots--;
                                    if (availableDemandSlots == 0)
                                    {
                                        break;
                                    }
                                }
                            }
                            if (availableDemandSlots == 0)
                            {
                                break;
                            }
                            // frame pacing (mirrors stock per-supply-inventory num++)
                            num++;
                            if (num == loopFrameLimit)
                            {
                                yield return null;
                                num = 0;
                                if (!Managers.GetManager<PlanetLoader>().GetIsLoaded())
                                {
                                    UpdatingRef(mgr) = false;
                                    yield break;
                                }
                                CompleteJob(mgr);
                            }
                        }

                        if (availableDemandSlots == 0)
                        {
                            continue;
                        }

                        // Pickable (spawned) objects lying in the world.
                        foreach (WorldObject item in allPickableWorldObject)
                        {
                            if (item.GetGroup() == demandedGroup
                                && !(item.GetGameObject() == null)
                                && !(item.GetPosition() == Vector3.zero)
                                && !(item.GetGameObject().GetComponentInChildren<ActionGrabable>() == null)
                                && item.GetGameObject().GetComponentInChildren<ActionGrabable>().GetCanGrab()
                                && item.GetPlanetHash() == demandPlanet
                                && CreateSpawned(mgr, demandInventory, item) != null)
                            {
                                availableDemandSlots--;
                                if (availableDemandSlots == 0)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    // Mark remaining demanded groups as claimed at this inventory's priority.
                    if (availableDemandSlots > 0)
                    {
                        foreach (Group currentDemandGroup in currentDemandGroups)
                        {
                            if (!demandPrio.TryGetValue(currentDemandGroup.stableHashCode, out var v2)
                                || v2 < demandInventory.GetLogisticEntity().GetPriority())
                            {
                                demandPrio[currentDemandGroup.stableHashCode] =
                                    demandInventory.GetLogisticEntity().GetPriority();
                            }
                        }
                    }

                    num++;
                    if (num == loopFrameLimit)
                    {
                        yield return null;
                        num = 0;
                        if (!Managers.GetManager<PlanetLoader>().GetIsLoaded())
                        {
                            UpdatingRef(mgr) = false;
                            yield break;
                        }
                        CompleteJob(mgr);
                    }
                }
                j++;
            }

            // --- Phase 2: retire completed / demand-full tasks ------------------
            var keysToDestroy = KeysToDestroyRef(mgr);
            keysToDestroy.Clear();
            foreach (var kv in allLogisticTasks)
            {
                if (kv.Value.GetDemandInventory().IsFull())
                {
                    kv.Value.SetTaskState(LogisticData.TaskState.Done);
                    keysToDestroy.Add(kv.Key);
                }
                else if (kv.Value.GetTaskState() == LogisticData.TaskState.Done)
                {
                    keysToDestroy.Add(kv.Key);
                }
            }
            foreach (int key in keysToDestroy)
            {
                allLogisticTasks.Remove(key);
            }

            yield return null;
            if (!Managers.GetManager<PlanetLoader>().GetIsLoaded())
            {
                UpdatingRef(mgr) = false;
                yield break;
            }
            CompleteJob(mgr);
            num = 0;

            // --- Phase 3: assign tasks to in-world drones -----------------------
            // CHANGE 2 (no cross-planet break) + CHANGE 3 (nearest-task tie-break).
            // Value copy (not a ref local - iterators forbid those). TransformAccessArray
            // is a handle struct; the copy reads the same native data (we only read).
            TransformAccessArray accessArray = AccessArrayRef(mgr);
            if (accessArray.isCreated)
            {
                var accessDrones = AccessDronesRef(mgr);
                var mode = SmartDronesPlugin.AssignMode.Value;
                int window = SmartDronesPlugin.NearestTieBreakAgeWindow.Value;

                j = 0;
                while (j < accessArray.length)
                {
                    if (accessArray[j] != null)
                    {
                        Drone drone = accessDrones[j];
                        if (drone != null && drone.GetLogisticTask() == null)
                        {
                            LogisticTask task = PickTaskForDrone(
                                mgr, allLogisticTasks, drone, mode, window, out bool sawAnyNotAttributed);
                            if (task != null)
                            {
                                // v0.4.2 CHANGE 5: cross-pool nearest preference. If an idle
                                // station drone sits strictly closer to this task's supply than
                                // THIS airborne drone, don't hand the task to the far airborne
                                // drone - leave it NotAttributed so Phase 4 releases the nearer
                                // station drone. The task still gets exactly one drone.
                                if (SmartDronesPlugin.PreferNearestIdleDrone.Value
                                    && TryDronePos(drone, out Vector3 dpos)
                                    && NearerIdleStationExists(mgr, task, dpos))
                                {
                                    if (SmartDronesPlugin.VerboseAssignmentLog.Value)
                                    {
                                        SmartDronesPlugin.Log?.LogDebug(
                                            SmartDronesPlugin.TimePrefix()
                                            + "defer task to nearer idle station drone "
                                            + "(airborne drone farther from supply)");
                                    }
                                    // leave task unassigned; Phase 4 releases a station drone.
                                }
                                else
                                {
                                    drone.SetLogisticTask(task, AllStationsRef(mgr));
                                }
                            }
                            else if (!sawAnyNotAttributed)
                            {
                                // Stock's optimization: no unattributed task exists ANYWHERE.
                                // (Stock broke whenever the per-planet query was null, which
                                // starved other planets - CHANGE 2 only breaks on a true global
                                // empty; a planet with no task is skip-and-continue.)
                                break;
                            }
                            // else: nothing for this drone's planet, but tasks remain
                            // elsewhere -> skip this drone, keep scanning.
                        }
                    }
                    num++;
                    if (num == loopFrameLimit)
                    {
                        yield return null;
                        num = 0;
                        if (!Managers.GetManager<PlanetLoader>().GetIsLoaded())
                        {
                            UpdatingRef(mgr) = false;
                            yield break;
                        }
                        CompleteJob(mgr);
                    }
                    j++;
                }

                yield return null;
                if (!Managers.GetManager<PlanetLoader>().GetIsLoaded())
                {
                    UpdatingRef(mgr) = false;
                    yield break;
                }
                CompleteJob(mgr);
            }

            // --- Phase 4: release stationed drones (nearest station to supply) --
            // CHANGE 4: unchanged from stock. Each task is independent (skip-and-
            // continue): an unservable task (e.g. no station on its planet) cannot
            // block release for other tasks.
            if (mgr.GetANonAttributedTask() != null)
            {
                var allStations = AllStationsRef(mgr);
                var stationsDist = StationsDistRef(mgr);
                foreach (var kv in allLogisticTasks)
                {
                    if (kv.Value.GetTaskState() != LogisticData.TaskState.NotAttributed)
                    {
                        continue;
                    }
                    LogisticTask task = kv.Value;
                    if (!task.GetIsSpawnedObject() && task.GetSupplyInventoryWorldObject() == null)
                    {
                        continue;
                    }
                    stationsDist.Clear();
                    foreach (MachineDroneStation station in allStations)
                    {
                        if (task.GetPlanetHash() == station.GetPlanetHash())
                        {
                            Vector3 b = task.GetIsSpawnedObject()
                                ? task.GetWorldObjectToMove().GetPosition()
                                : task.GetSupplyInventoryWorldObject().GetPosition();
                            int d = Mathf.RoundToInt(Vector3.Distance(station.gameObject.transform.position, b));
                            stationsDist.Add(new LogisticStationDistanceToTask(station, d));
                        }
                    }
                    stationsDist.Sort();
                    foreach (LogisticStationDistanceToTask sd in stationsDist)
                    {
                        GameObject go = sd.GetMachineDroneStation().TryToReleaseOneDrone();
                        if (go != null)
                        {
                            Drone d = go.GetComponentInChildren<Drone>();
                            d.AddDroneToFleet();
                            d.SetLogisticTask(task, allStations);
                            break;
                        }
                    }
                }
            }

            UpdatingRef(mgr) = false;
        }

        // v0.4.2: true if a drone station on the task's planet holds at least one idle
        // drone AND sits strictly closer to the task's supply than dronePos. Non-mutating
        // (GetNumberOfDronesInStation; never releases). Returns false on any unknown
        // position so a bad read can never deadlock a task - stock airborne assignment then
        // proceeds. Both pools do the same supply->demand leg, so distance-to-supply is the
        // fair comparison.
        private static bool NearerIdleStationExists(LogisticManager mgr, LogisticTask task, Vector3 dronePos)
        {
            WorldObject supplyWo = task.GetIsSpawnedObject()
                ? task.GetWorldObjectToMove()
                : task.GetSupplyInventoryWorldObject();
            if (!TryPos(supplyWo, out Vector3 supplyPos))
            {
                return false;
            }
            float droneDist = Vector3.Distance(dronePos, supplyPos);
            int planet = task.GetPlanetHash();
            foreach (MachineDroneStation station in AllStationsRef(mgr))
            {
                if (station == null || station.GetPlanetHash() != planet)
                {
                    continue;
                }
                int idle;
                try { idle = station.GetNumberOfDronesInStation(); } catch { continue; }
                if (idle <= 0)
                {
                    continue;
                }
                Vector3 sp;
                try { sp = station.gameObject.transform.position; } catch { continue; }
                if (Vector3.Distance(sp, supplyPos) < droneDist)
                {
                    return true;
                }
            }
            return false;
        }

        private static int CompareSupply(SupplyCand a, SupplyCand b)
        {
            int c = a.Dist.CompareTo(b.Dist);
            return c != 0 ? c : a.Order.CompareTo(b.Order); // stable: keep stock order on ties
        }

        // Internal nearest/FIFO task picker (replaces per-planet GetANonAttributedTask
        // in phase 3 so we can tie-break by the drone's position). Respects _paused
        // exactly like GetANonAttributedTask (returns null while paused).
        private static LogisticTask PickTaskForDrone(
            LogisticManager mgr, Dictionary<int, LogisticTask> all, Drone drone,
            AssignmentMode mode, int window, out bool sawAnyNotAttributed)
        {
            sawAnyNotAttributed = false;
            if (PausedRef(mgr))
            {
                return null;
            }
            int planetHash = drone.GetDronePlanetHash();

            long nowSeq = SmartDronesPlugin.Seq;
            _taskBuf.Clear();
            foreach (var kv in all)
            {
                LogisticTask t = kv.Value;
                if (t.GetTaskState() != LogisticData.TaskState.NotAttributed)
                {
                    continue;
                }
                sawAnyNotAttributed = true; // any planet
                if (planetHash != 0 && t.GetPlanetHash() != planetHash)
                {
                    continue;
                }
                long stamp = Patches.GetAge(t);
                int prio = Patches.GetDemandPriority(t);
                long ageTicks = nowSeq - stamp;
                if (ageTicks < 0) ageTicks = 0; // task stamped lazily after nowSeq was read
                long score = (long)prio * SmartDronesPlugin.PriorityWeight.Value
                             + ageTicks * SmartDronesPlugin.AgeWeight.Value;
                _taskBuf.Add(new TaskCand { Task = t, Age = stamp, Prio = prio, AgeTicks = ageTicks, Score = score });
            }

            if (_taskBuf.Count == 0)
            {
                return null;
            }

            bool haveDronePos = TryDronePos(drone, out Vector3 dronePos);

            LogisticTask chosen;
            if (mode == AssignmentMode.PriorityThenAge)
            {
                chosen = BestByScore(haveDronePos, dronePos);
            }
            else if (mode == AssignmentMode.NearestFirst && haveDronePos)
            {
                chosen = NearestBySupply(dronePos, 0);
            }
            else if (mode == AssignmentMode.FifoWithNearestTieBreak && haveDronePos)
            {
                long minAge = long.MaxValue;
                for (int i = 0; i < _taskBuf.Count; i++)
                {
                    if (_taskBuf[i].Age < minAge) minAge = _taskBuf[i].Age;
                }
                chosen = NearestBySupply(dronePos, minAge + window); // only tasks age<=minAge+window eligible
            }
            else
            {
                // FIFO (also the fallback when drone position is unavailable).
                chosen = OldestTask();
            }

            if (SmartDronesPlugin.VerboseAssignmentLog.Value)
            {
                string taskGroup = "none";
                string scoreDetail = "";
                if (chosen != null)
                {
                    try { var wo = chosen.GetWorldObjectToMove(); if (wo != null) taskGroup = SmartDronesPlugin.GroupName(wo.GetGroup()); } catch { }
                    for (int i = 0; i < _taskBuf.Count; i++)
                    {
                        if (_taskBuf[i].Task == chosen)
                        {
                            scoreDetail = " prio=" + _taskBuf[i].Prio + " age=" + _taskBuf[i].AgeTicks
                                        + " score=" + _taskBuf[i].Score;
                            break;
                        }
                    }
                }
                SmartDronesPlugin.Log?.LogDebug(
                    SmartDronesPlugin.TimePrefix() + "assign drone(planet=" + planetHash + ") mode=" + mode +
                    " candidates=" + _taskBuf.Count + " -> " +
                    (chosen == null ? "none" : ("task group=" + taskGroup + scoreDetail)));
            }
            return chosen;
        }

        private static LogisticTask OldestTask()
        {
            LogisticTask best = null;
            long bestAge = long.MaxValue;
            for (int i = 0; i < _taskBuf.Count; i++)
            {
                if (_taskBuf[i].Age < bestAge)
                {
                    bestAge = _taskBuf[i].Age;
                    best = _taskBuf[i].Task;
                }
            }
            return best;
        }

        // Nearest task supply to dronePos. If ageCeiling > 0, only tasks whose age is
        // <= ageCeiling are eligible (FIFO tie-break window); ties broken by age.
        private static LogisticTask NearestBySupply(Vector3 dronePos, long ageCeiling)
        {
            LogisticTask best = null;
            float bestDist = float.MaxValue;
            long bestAge = long.MaxValue;
            for (int i = 0; i < _taskBuf.Count; i++)
            {
                TaskCand c = _taskBuf[i];
                if (ageCeiling > 0 && c.Age > ageCeiling)
                {
                    continue;
                }
                float dist = SupplyDist(c.Task, dronePos);
                if (dist < bestDist || (dist == bestDist && c.Age < bestAge))
                {
                    bestDist = dist;
                    bestAge = c.Age;
                    best = c.Task;
                }
            }
            // If the window excluded everything (shouldn't happen - minAge is in it),
            // fall back to oldest.
            return best ?? OldestTask();
        }

        // PriorityThenAge picker: highest Score wins; tie-break nearest supply to the
        // drone (when its position is known), then oldest (higher age-sequence stamp,
        // i.e. lower Age value). Score already folds priority and unbounded age, so
        // this is the anti-starvation core: a long-waiting low-prio task's Score keeps
        // climbing until it overtakes any fixed priority gap.
        private static LogisticTask BestByScore(bool haveDronePos, Vector3 dronePos)
        {
            LogisticTask best = null;
            long bestScore = long.MinValue;
            float bestDist = float.MaxValue;
            long bestAge = long.MaxValue; // lower stamp == older == preferred on final tie
            for (int i = 0; i < _taskBuf.Count; i++)
            {
                TaskCand c = _taskBuf[i];
                float dist = haveDronePos ? SupplyDist(c.Task, dronePos) : 0f;
                bool better =
                    best == null
                    || c.Score > bestScore
                    || (c.Score == bestScore && haveDronePos && dist < bestDist)
                    || (c.Score == bestScore && (!haveDronePos || dist == bestDist) && c.Age < bestAge);
                if (better)
                {
                    best = c.Task;
                    bestScore = c.Score;
                    bestDist = dist;
                    bestAge = c.Age;
                }
            }
            return best;
        }

        // Distance from dronePos to a task's supply (spawned object or supply container).
        private static float SupplyDist(LogisticTask t, Vector3 dronePos)
        {
            WorldObject supplyWo = t.GetIsSpawnedObject()
                ? t.GetWorldObjectToMove()
                : t.GetSupplyInventoryWorldObject();
            return TryPos(supplyWo, out Vector3 sp) ? Vector3.Distance(dronePos, sp) : float.MaxValue;
        }

        // ---- Position helpers (never throw; null GameObject -> stored position -> fail) ----
        private static bool TryPos(WorldObject wo, out Vector3 p)
        {
            p = default;
            if (wo == null)
            {
                return false;
            }
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
            return false;
        }

        private static bool TryInvPos(Inventory inv, out Vector3 p)
        {
            p = default;
            if (inv == null)
            {
                return false;
            }
            LogisticEntity e = inv.GetLogisticEntity();
            if (e == null)
            {
                return false;
            }
            return TryPos(e.GetWorldObject(), out p);
        }

        private static bool TryDronePos(Drone drone, out Vector3 p)
        {
            p = default;
            if (drone == null)
            {
                return false;
            }
            try
            {
                GameObject go = drone.gameObject;
                if (go != null)
                {
                    p = go.transform.position;
                    return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }
}
