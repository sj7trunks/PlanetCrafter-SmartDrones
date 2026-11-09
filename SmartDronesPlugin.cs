using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SpaceCraft;

namespace SmartDrones
{
    [BepInPlugin(PluginGuid, "SmartDrones", "0.4.0")]
    public class SmartDronesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "io.github.sj7trunks.smartdrones";

        internal static ManualLogSource Log;

        // v0.1 toggles.
        internal static ConfigEntry<bool> EnableStarvationFix;     // GetANonAttributedTask oldest-first prefix
        internal static ConfigEntry<bool> VerboseAssignmentLog;

        // v0.2: full coroutine replacement.
        internal static ConfigEntry<bool> FullDispatchReplacement;

        // v0.3: dual-role logistic containers.
        internal static ConfigEntry<bool> AllowDualRoleGroups;
        internal static ConfigEntry<AssignmentMode> AssignMode;
        internal static ConfigEntry<int> NearestTieBreakAgeWindow;

        // v0.4: priority-aware pickup with anti-starvation aging.
        internal static ConfigEntry<int> PriorityWeight;
        internal static ConfigEntry<int> AgeWeight;

        // Creation-order ("age") tracking for LogisticTask instances.
        // ConditionalWeakTable so dead tasks are GC'd with no leak; value is a
        // mutable box holding a monotonic sequence number (lower == older).
        internal static readonly ConditionalWeakTable<LogisticTask, StrongBox<long>> TaskAge =
            new ConditionalWeakTable<LogisticTask, StrongBox<long>>();
        internal static long Seq = 0;

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        internal static string TimePrefix()
        {
            return "[t=" + UnityEngine.Time.time.ToString("F1") + "] ";
        }

        internal static string GroupName(Group g)
        {
            if (g == null) return "null";
            try { string id = g.GetId(); if (!string.IsNullOrEmpty(id)) return id; } catch { }
            return g.stableHashCode.ToString();
        }

        private void Awake()
        {
            Log = Logger;

            EnableStarvationFix = Config.Bind(
                "Patches", "StarvationFixNearestTask", true,
                "Replace LogisticManager.GetANonAttributedTask with an oldest-first (FIFO) picker. "
                + "Fixes the task-starvation bug where dictionary bucket reuse under churn makes new "
                + "tasks jump the queue and old tasks never get serviced. Still used by callers OUTSIDE "
                + "our coroutine (Drone.EndAssignement, station-release existence probe) even when "
                + "FullDispatchReplacement is on.");

            FullDispatchReplacement = Config.Bind(
                "Patches", "FullDispatchReplacement", true,
                "Replace the ENTIRE SetLogisticTasks dispatch coroutine with SmartDrones' reimplementation "
                + "(nearest-supply binding, no cross-planet break, nearest-task tie-break). When false, the "
                + "stock coroutine runs and only the v0.1 oldest-first prefix is active. Auto-disabled after "
                + "3 consecutive pass failures (game never loses dispatch).");

            AllowDualRoleGroups = Config.Bind(
                "Patches", "AllowDualRoleGroups", true,
                "Allow a logistic container to list the SAME item group as BOTH supply and demand "
                + "(a 'dual-role' buffer). Vanilla silently strips such overlaps via LogisticEntity."
                + "SanitizeGroups; this Harmony-prefixes that method to a no-op so the overlap survives "
                + "editing and save/load. Enables buffer->shuttle staging flows. Only safe with "
                + "FullDispatchReplacement=true, which carries the anti-ping-pong guard that stops two "
                + "equal-priority dual-role containers from draining each other forever.");

            AssignMode = Config.Bind(
                "Dispatch", "AssignmentMode", AssignmentMode.PriorityThenAge,
                "How an available drone picks among unassigned tasks on its planet (FullDispatchReplacement only). "
                + "PriorityThenAge (default) = highest score wins, score = demandPriority*PriorityWeight + "
                + "ageTicks*AgeWeight, nearest-supply tie-break; higher-priority demands served first but aging "
                + "guarantees no low-priority task starves. FIFO = strictly oldest-first. NearestFirst = closest "
                + "supply to the drone, ignoring age. FifoWithNearestTieBreak = oldest-first, but tasks within "
                + "NearestTieBreakAgeWindow of the oldest are tie-broken by nearest supply to the drone.");

            NearestTieBreakAgeWindow = Config.Bind(
                "Dispatch", "NearestTieBreakAgeWindow", 50,
                "FifoWithNearestTieBreak only: tasks whose creation-sequence age is within this many steps of "
                + "the oldest eligible task are treated as 'same batch' and tie-broken by distance. Larger = more "
                + "nearest-biased; smaller = more strictly FIFO.");

            PriorityWeight = Config.Bind(
                "Dispatch", "PriorityWeight", 1000,
                "PriorityThenAge only: how many 'task-creations of waiting' one priority LEVEL is worth. "
                + "score = demandPriority*PriorityWeight + ageTicks*AgeWeight, where ageTicks = number of logistic "
                + "tasks created since this task was queued. At the default 1000, a priority-0 task overtakes a "
                + "FRESH priority-1 task after ~1000 task-creations of waiting (seconds-to-minutes of real time at "
                + "typical churn), so NOTHING starves. Lower = aging bites sooner (more FIFO-like); higher = "
                + "priority dominates longer before age can rescue a low-priority task.");

            AgeWeight = Config.Bind(
                "Dispatch", "AgeWeight", 1,
                "PriorityThenAge only: score contribution per unit of age (one task-creation elapsed). Default 1 "
                + "pairs with PriorityWeight=1000. Raising it (relative to PriorityWeight) makes aging overtake "
                + "priority gaps faster; the ratio PriorityWeight/AgeWeight is the real knob (task-creations of "
                + "wait per priority level before a lower-priority task wins).");

            VerboseAssignmentLog = Config.Bind(
                "Logging", "VerboseAssignmentLog", false,
                "Log each binding/assignment decision (with distances) at Debug level. Spammy; "
                + "leave off unless diagnosing.");

            // Always track task age (cheap, needed by both the prefix and the reimplementation).
            var ctor = AccessTools.Constructor(typeof(LogisticTask), new[]
            {
                typeof(WorldObject), typeof(Inventory), typeof(Inventory),
                typeof(WorldObject), typeof(WorldObject), typeof(bool)
            });
            if (ctor == null)
            {
                Log.LogError("Could not resolve LogisticTask(.ctor, 6 args) - age tracking disabled; "
                           + "ordering will fall back to first-seen order.");
            }
            else
            {
                _harmony.Patch(ctor, postfix: new HarmonyMethod(
                    typeof(Patches), nameof(Patches.LogisticTaskCtorPostfix)));
                Log.LogInfo("Applied patch: LogisticTask.ctor postfix (task age tracking).");
            }

            // Announce the active task-pickup policy so the log shows what's live.
            Log.LogInfo("Dispatch policy: AssignmentMode=" + AssignMode.Value
                + " PriorityWeight=" + PriorityWeight.Value + " AgeWeight=" + AgeWeight.Value
                + (AssignMode.Value == AssignmentMode.PriorityThenAge
                    ? " (priority-0 task overtakes a fresh priority-1 task after ~"
                      + (AgeWeight.Value > 0 ? (PriorityWeight.Value / AgeWeight.Value).ToString() : "inf")
                      + " task-creations of waiting -> no starvation)"
                    : ""));

            // v0.1 oldest-first prefix on GetANonAttributedTask. Kept even under the
            // full replacement: our coroutine uses its own internal picker for phase-3
            // drone assignment (so it can see drone position), but GetANonAttributedTask
            // is still hit by Drone.EndAssignement, SetANonAttributedTaskToDrone and the
            // phase-4 station-release existence probe - all benefit from oldest-first.
            if (EnableStarvationFix.Value)
            {
                var target = AccessTools.Method(typeof(LogisticManager),
                    nameof(LogisticManager.GetANonAttributedTask));
                if (target == null)
                {
                    Log.LogError("Could not resolve LogisticManager.GetANonAttributedTask - "
                               + "starvation fix NOT applied.");
                }
                else
                {
                    _harmony.Patch(target, prefix: new HarmonyMethod(
                        typeof(Patches), nameof(Patches.GetANonAttributedTaskPrefix)));
                    Log.LogInfo("Applied patch: GetANonAttributedTask prefix (oldest-first).");
                }
            }
            else
            {
                Log.LogInfo("StarvationFixNearestTask disabled via config - GetANonAttributedTask left stock.");
            }

            // v0.2 full coroutine replacement. Patch the dispatch-coroutine kickoff method
            // SetLogisticTasks(); our prefix substitutes an IEnumerator via __result and
            // returns false, so the stock state machine is never instantiated (unless
            // FullDispatchReplacement is toggled off at runtime).
            var setTasks = AccessTools.Method(typeof(LogisticManager), "SetLogisticTasks");
            if (setTasks == null)
            {
                Log.LogError("Could not resolve LogisticManager.SetLogisticTasks - "
                           + "full dispatch replacement NOT applied; running v0.1 behavior only.");
            }
            else
            {
                _harmony.Patch(setTasks, prefix: new HarmonyMethod(
                    typeof(Dispatch), nameof(Dispatch.SetLogisticTasksPrefix)));
                Log.LogInfo("Applied patch: SetLogisticTasks prefix (full dispatch replacement).");
            }

            // v0.3 dual-role: prefix LogisticEntity.SanitizeGroups so the supply/demand
            // overlap is no longer stripped. One call site gates all four setters and the
            // save-load path, so a single prefix is sufficient.
            if (AllowDualRoleGroups.Value)
            {
                var sanitize = AccessTools.Method(typeof(LogisticEntity), "SanitizeGroups");
                if (sanitize == null)
                {
                    Log.LogError("Could not resolve LogisticEntity.SanitizeGroups - dual-role NOT enabled; "
                               + "vanilla will keep stripping supply/demand group overlap.");
                }
                else
                {
                    _harmony.Patch(sanitize, prefix: new HarmonyMethod(
                        typeof(Patches), nameof(Patches.SanitizeGroupsPrefix)));
                    Log.LogInfo("Applied patch: LogisticEntity.SanitizeGroups prefix (dual-role groups allowed).");
                }

                if (!FullDispatchReplacement.Value)
                {
                    Log.LogWarning("AllowDualRoleGroups is ON but FullDispatchReplacement is OFF. The stock "
                                 + "dispatch coroutine has NO anti-ping-pong guard, so two dual-role containers "
                                 + "sharing a group at equal priority can drain each other endlessly. Enable "
                                 + "FullDispatchReplacement, or keep dual-role buffers strictly lower-priority "
                                 + "than their consumers.");
                }
            }
            else
            {
                Log.LogInfo("AllowDualRoleGroups disabled via config - vanilla SanitizeGroups left intact.");
            }

            Log.LogInfo("SmartDrones 0.4.0 loaded. FullDispatchReplacement="
                      + FullDispatchReplacement.Value + ", AssignmentMode=" + AssignMode.Value
                      + ", DualRole=" + AllowDualRoleGroups.Value
                      + (FullDispatchReplacement.Value
                            ? " (nearest-supply binding + no cross-planet break + nearest-task tie-break"
                              + (AllowDualRoleGroups.Value ? " + anti-pingpong guard" : "") + " active)"
                            : " (stock coroutine + oldest-first prefix only)"));
        }
    }
}
