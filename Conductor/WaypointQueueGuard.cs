using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Model;
using Game.Messages;
using Model.AI;
using UI.Common;
using UI.EngineControls;

namespace Conductor
{
    // Waypoint Queue (PrinceOfPluto's mod, optional) keeps its own queue of waypoints per engine, and
    // looping routes that refill it. When an engine Conductor is driving also has a queue or a route
    // there, Waypoint Queue sends its own waypoints and Conductor hands the train over. So while
    // Conductor is driving an engine, Waypoint Queue's add, insert and apply-route calls are refused
    // for it, and any queue or route it already had is cleared. Other engines are left to Waypoint Queue.
    internal static class WaypointQueueGuard
    {
        private static bool _tried;
        private static PropertyInfo _shared;
        private static PropertyInfo _states, _assignments;
        private static MethodInfo _removeState, _removeAssignment;

        // Waypoint Queue may load after Conductor, so this is done on the first tick, not at load.
        public static void Setup(Harmony harmony)
        {
            if (_tried)
                return;
            _tried = true;
            try
            {
                Type controller = AccessTools.TypeByName("WaypointQueue.WaypointQueueController");
                Type manager = AccessTools.TypeByName("WaypointQueue.State.ModStateManager");
                if (controller == null || manager == null)
                    return;
                var block = new HarmonyMethod(AccessTools.Method(typeof(WaypointQueueGuard), nameof(Block)));
                int patched = 0;
                foreach (string name in new[] { "AddWaypoint", "InsertWaypoint", "AddWaypointsFromRoute" })
                {
                    MethodInfo m = AccessTools.Method(controller, name);
                    if (m != null && m.GetParameters().Length > 0 && typeof(BaseLocomotive).IsAssignableFrom(m.GetParameters()[0].ParameterType))
                    {
                        harmony.Patch(m, prefix: block);
                        patched++;
                    }
                }
                _shared = AccessTools.Property(manager, "Shared");
                _states = AccessTools.Property(manager, "LocoWaypointStates");
                _assignments = AccessTools.Property(manager, "RouteAssignments");
                _removeState = AccessTools.Method(manager, "RemoveLocoWaypointState", new[] { typeof(string) });
                _removeAssignment = AccessTools.Method(manager, "RemoveRouteAssignment", new[] { typeof(string) });
                // Waypoint Queue cancels an engine's current waypoint whenever it drops that engine's
                // queue, which it does on its own once a queue is empty (an open Waypoint Queue window
                // can leave one behind). For an engine Conductor is driving, that waypoint is Conductor's.
                Type service = AccessTools.TypeByName("WaypointQueue.Services.AutoEngineerService");
                MethodInfo cancel = service != null ? AccessTools.Method(service, "CancelActiveOrders") : null;
                if (cancel != null && cancel.GetParameters().Length == 1 && typeof(BaseLocomotive).IsAssignableFrom(cancel.GetParameters()[0].ParameterType))
                {
                    harmony.Patch(cancel, prefix: new HarmonyMethod(AccessTools.Method(typeof(WaypointQueueGuard), nameof(KeepOurs))));
                    patched++;
                }
                Journal.Info($"Conductor: Waypoint Queue found; it can't give orders to engines Conductor is driving or cancel theirs ({patched} of 4 hooks).");
            }
            catch (Exception ex)
            {
                Journal.Warning("Conductor: couldn't hook Waypoint Queue: " + ex.Message);
            }
        }

        private static bool Block(object[] __args)
        {
            var loco = __args.Length > 0 ? __args[0] as BaseLocomotive : null;
            if (!Conducted.Is(loco))
                return true;
            // Once in a while per engine: Waypoint Queue retries a looping route every tick.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_toasted.TryGetValue(loco.id, out float last) || now - last > 30f)
            {
                _toasted[loco.id] = now;
                Toast.Present($"Conductor is driving {loco.DisplayName}. Stop it in the Conductor window to give it Waypoint Queue orders.");
            }
            return false;
        }

        private static readonly System.Collections.Generic.Dictionary<string, float> _toasted = new System.Collections.Generic.Dictionary<string, float>();

        private static bool KeepOurs(object[] __args) => !Conducted.Is(__args.Length > 0 ? __args[0] as BaseLocomotive : null);

        // Clears any Waypoint Queue queue or route for an engine Conductor is driving, keeping
        // Conductor's own waypoint (clearing the queue makes Waypoint Queue cancel the current one).
        public static void Release(BaseLocomotive loco, Plan plan)
        {
            if (_shared == null)
                return;
            try
            {
                object manager = _shared.GetValue(null);
                if (manager == null)
                    return;
                bool queued = _states?.GetValue(manager) is IDictionary states && states.Contains(loco.id);
                bool routed = _assignments?.GetValue(manager) is IDictionary routes && routes.Contains(loco.id);
                if (!queued && !routed)
                    return;
                var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
                OrderWaypoint? keep = ae.Mode == AutoEngineerMode.Waypoint && ae.Orders.Waypoint.HasValue && ae.Orders.Waypoint.Value.LocationString == plan.Sent
                    ? ae.Orders.Waypoint : null;
                int mph = ae.Orders.MaxSpeedMph;
                if (routed)
                    _removeAssignment?.Invoke(manager, new object[] { loco.id });
                if (queued)
                    _removeState?.Invoke(manager, new object[] { loco.id });
                if (keep.HasValue)
                {
                    var after = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
                    if (!after.Orders.Waypoint.HasValue)
                        after.SetWaypointOrder(keep.Value, mph);
                }
                Journal.Info($"Conductor: {loco.DisplayName} had Waypoint Queue {(queued && routed ? "waypoints and a route" : queued ? "waypoints" : "a route")}; cleared them so they don't take over.");
            }
            catch (Exception ex)
            {
                Journal.Warning($"Conductor: couldn't clear Waypoint Queue's orders for {loco.DisplayName}: {ex.Message}");
            }
        }
    }
}
