using System;
using System.Linq;
using HarmonyLib;
using Model;
using Model.AI;
using Track;

namespace Conductor
{
    // Writes to the log why the Auto Engineer turned down a Conductor waypoint, so a
    // "couldn't get into" report can be traced from Player.log.
    [HarmonyPatch(typeof(AutoEngineerPlanner), nameof(AutoEngineerPlanner.TryPlanRoute))]
    internal static class LogRouteRefusals
    {
        private static void Postfix(BaseLocomotive locomotive, OrderWaypoint waypoint, bool __result, string message, float routeDistance)
        {
            try
            {
                if (!Conducted.Is(locomotive))
                    return;
                if (__result)
                    return;
                Journal.Warning($"Conductor: the Auto Engineer can't plan {locomotive.DisplayName}'s route to {waypoint.LocationString}: {message ?? "(no reason given)"} {Where(locomotive, waypoint.LocationString)}");
            }
            catch (Exception ex)
            {
                Journal.Warning("Conductor: route log failed: " + ex.Message);
            }
        }

        // The train's ends and the waypoint, with the segments they're on, for reading the log against the map.
        private static string Where(BaseLocomotive loco, string locationString)
        {
            Graph graph = Graph.Shared;
            var cars = loco.EnumerateCoupled().ToList();
            Location a = cars[0].LocationFor(Car.LogicalEnd.A), b = cars[cars.Count - 1].LocationFor(Car.LogicalEnd.B);
            string target;
            try
            {
                Location t = graph.ResolveLocationString(locationString);
                target = $"{t} at {graph.GetPosition(t)}";
            }
            catch (Exception ex)
            {
                target = "unresolvable (" + ex.Message + ")";
            }
            return $"[train ends {a} at {graph.GetPosition(a)} and {b} at {graph.GetPosition(b)}; waypoint {target}]";
        }
    }
}
