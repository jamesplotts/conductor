using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
using Model.Ops;
using Track;
using UnityEngine;

namespace Conductor
{
    // Where a train should stop at a station. Where a station has a passing siding as well as the
    // main line, the train takes the siding (if it fits clear of both switches) and leaves the main open.
    internal static class Platforms
    {
        // Whether the last StopAt chose a siding, for the status line.
        public static bool LastWasSiding;

        public static Location? StopAt(PassengerStop stop, BaseLocomotive loco, bool preferSiding)
        {
            LastWasSiding = false;
            List<TrackSpan> spans = stop.TrackSpans.Where(s => s != null && s.lower.HasValue && s.upper.HasValue).ToList();
            if (spans.Count == 0)
                return null;
            Graph graph = Graph.Shared;
            List<Car> train = loco.EnumerateCoupled().ToList();
            float length = AutoEngineerPlanner.CalculateTotalLength(train);
            if (preferSiding && spans.Count > 1)
            {
                var sidings = spans.Select(s => (span: s, siding: Siding(graph, s))).Where(x => x.siding != null && x.siding.Room >= length)
                    .OrderByDescending(x => x.siding.Score).ThenByDescending(x => x.span.Length).ToList();
                // Only when another platform track is the main line, so there's something to keep clear.
                if (sidings.Count > 0 && spans.Any(s => s != sidings[0].span && (Siding(graph, s)?.Score ?? 0) < sidings[0].siding.Score))
                {
                    LastWasSiding = true;
                    return InSiding(graph, stop, sidings[0].span, sidings[0].siding, train, length, Overshoot(graph, train, sidings[0].span.GetCenterPoint()));
                }
            }
            // Otherwise the main line: of the platform tracks, the least siding-like (a siding is reached
            // by the diverging route), the longest of those. With one platform track, that one.
            TrackSpan longest = spans.Count == 1 ? spans[0]
                : spans.OrderBy(s => Siding(graph, s)?.Score ?? 0).ThenByDescending(s => s.Length).First();
            return Alongside(graph, stop, longest, Trailing(graph, train, longest.GetCenterPoint()), length, Overshoot(graph, train, longest.GetCenterPoint()));
        }

        // Where to stop so the coaches sit alongside the station building: centered on it where the
        // platform track allows, otherwise as near as the platform's ends let them. Falls back to the
        // far end of the platform when the building isn't loaded (it's only there near the camera's tiles).
        private static Location Alongside(Graph graph, PassengerStop stop, TrackSpan span, Vector3 trailing, float length, float overshoot)
        {
            Location farEnd = FarEnd(graph, span, trailing, overshoot);
            Vector3? depot = Depot(stop);
            if (!depot.HasValue)
                return farEnd;
            Location lower = span.lower.Value, upper = span.upper.Value;
            bool upperFar = Fuel.Flat(graph.GetPosition(upper), trailing) >= Fuel.Flat(graph.GetPosition(lower), trailing);
            Location far = upperFar ? upper : lower, near = upperFar ? lower : upper;
            Location from = graph.LocationOrientedToward(near, far);
            HashSet<TrackSegment> onSpan = new HashSet<TrackSegment>(span.GetSegments());
            float total = span.Length, best = -1f, bestGap = float.MaxValue;
            for (float d = 0f; d <= total; d += 2f)
            {
                Location at = graph.LocationByMoving(from, d, false, true);
                if (!onSpan.Contains(at.segment))
                    break;
                float gap = Fuel.Flat(graph.GetPosition(at), depot.Value);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = d;
                }
            }
            if (best < 0f || bestGap > 150f)
                return farEnd;
            float coaches = Mathf.Max(0f, length - overshoot);
            float inset = Mathf.Min(5f, total / 4f);
            float farMost = total - inset + Mathf.Min(overshoot, RoomPast(graph, far, near, overshoot));
            // The engine end goes this far along: the coaches' middle at the building, but the whole
            // train on the platform track where it fits, and never past the far end.
            float target = Mathf.Max(best + coaches / 2f + overshoot, Mathf.Min(inset + coaches + overshoot, farMost));
            if (target >= farMost)
                return farEnd;
            Location spot = graph.LocationByMoving(from, target, false, true);
            return onSpan.Contains(spot.segment) || target > total ? spot : farEnd;
        }

        private static readonly AccessTools.FieldRef<StationAgent, PassengerStop> AgentStop =
            AccessTools.FieldRefAccess<StationAgent, PassengerStop>("_passengerStop");

        private static readonly Dictionary<string, StationAgent> Agents = new Dictionary<string, StationAgent>();
        private static float _agentsAt = -999f;

        // The station building (its agent), in game coordinates, when it's loaded.
        private static Vector3? Depot(PassengerStop stop)
        {
            if ((!Agents.TryGetValue(stop.identifier, out StationAgent agent) || agent == null) && Time.realtimeSinceStartup - _agentsAt > 5f)
            {
                _agentsAt = Time.realtimeSinceStartup;
                Agents.Clear();
                foreach (StationAgent a in Object.FindObjectsOfType<StationAgent>())
                {
                    PassengerStop s = AgentStop(a);
                    if (s != null)
                        Agents[s.identifier] = a;
                }
                Agents.TryGetValue(stop.identifier, out agent);
            }
            return agent != null ? WorldTransformer.WorldToGame(agent.transform.position) : (Vector3?)null;
        }

        // A platform track between two switches.
        private sealed class SidingInfo
        {
            public TrackNode EndA, EndB;
            // The segment entering each switch from the siding side.
            public TrackSegment LastA, LastB;
            // Room between the switches, clear of both, in meters.
            public float Room;
            // How siding-like: reached by the diverging route at either end, and lower route priority.
            public int Score;
        }

        private static SidingInfo Siding(Graph graph, TrackSpan span)
        {
            TrackSegment segment = span.GetSegments().FirstOrDefault();
            if (segment == null)
                return null;
            Wyes.Run towardA = Wyes.Walk(graph, segment.b, segment);
            Wyes.Run towardB = Wyes.Walk(graph, segment.a, segment);
            if (towardA.DeadEnd || towardB.DeadEnd || towardA.End == null || towardB.End == null || towardA.End == towardB.End)
                return null;
            int diverging = 0;
            foreach (Wyes.Run run in new[] { towardA, towardB })
            {
                if (!graph.IsSwitch(run.End) || !graph.DecodeSwitchAt(run.End, out TrackSegment enter, out _, out _) || run.Last == enter)
                    return null;
                graph.SegmentsReachableFrom(enter, enter.EndForNode(run.End), out TrackSegment normal, out TrackSegment reversed);
                if (run.Last == reversed)
                    diverging++;
            }
            return new SidingInfo
            {
                EndA = towardA.End,
                LastA = towardA.Last,
                EndB = towardB.End,
                LastB = towardB.Last,
                Room = towardA.Length + towardB.Length - segment.GetLength() - 2f * Wyes.Clearance,
                Score = diverging * 2 - segment.priority,
            };
        }

        // In a siding: at the far end of the platform when the whole train then fits clear of the
        // near switch, otherwise just far enough on to clear it (never past the far switch's clearance point).
        private static Location InSiding(Graph graph, PassengerStop stop, TrackSpan span, SidingInfo siding, List<Car> train, float length, float overshoot)
        {
            Vector3 trailing = Trailing(graph, train, span.GetCenterPoint());
            bool aIsFar = Fuel.Flat(siding.EndA.Position, trailing) >= Fuel.Flat(siding.EndB.Position, trailing);
            Location farClear = aIsFar ? Wyes.Along(graph, siding.EndA, siding.LastA, Wyes.Clearance) : Wyes.Along(graph, siding.EndB, siding.LastB, Wyes.Clearance);
            Location nearClear = aIsFar ? Wyes.Along(graph, siding.EndB, siding.LastB, Wyes.Clearance) : Wyes.Along(graph, siding.EndA, siding.LastA, Wyes.Clearance);
            Location platformEnd = Alongside(graph, stop, span, trailing, length, overshoot);
            Vector3 near = graph.GetPosition(nearClear);
            // Not past the far switch's clear point, however far the engine runs on.
            if (Fuel.Flat(near, graph.GetPosition(platformEnd)) > Fuel.Flat(near, graph.GetPosition(farClear)))
                return farClear;
            if (Fuel.Flat(near, graph.GetPosition(platformEnd)) >= length)
                return platformEnd;
            // Too long to stop at the platform's end clear of the near switch: pull up just far enough
            // to clear it, which keeps as much of the train at the platform as can be (not on to the far switch).
            Location justClear = aIsFar ? Wyes.Along(graph, siding.EndB, siding.LastB, Wyes.Clearance + length + 2f) : Wyes.Along(graph, siding.EndA, siding.LastA, Wyes.Clearance + length + 2f);
            return Fuel.Flat(near, graph.GetPosition(justClear)) < Fuel.Flat(near, graph.GetPosition(farClear)) ? justClear : farClear;
        }

        // Near the far end of a track span, seen from the train, so the whole train pulls in along it.
        public static Location FarEndFor(TrackSpan span, BaseLocomotive loco)
        {
            Graph graph = Graph.Shared;
            return FarEnd(graph, span, Trailing(graph, loco.EnumerateCoupled().ToList(), span.GetCenterPoint()));
        }

        // Near the far end of a platform track, seen from the end of the train that comes in last,
        // or past it by overshoot: far enough that the coaches, not the engine, reach the platform's end.
        private static Location FarEnd(Graph graph, TrackSpan span, Vector3 trailing, float overshoot = 0f)
        {
            Location lower = span.lower.Value, upper = span.upper.Value;
            bool upperFar = Fuel.Flat(graph.GetPosition(upper), trailing) >= Fuel.Flat(graph.GetPosition(lower), trailing);
            Location far = upperFar ? upper : lower, near = upperFar ? lower : upper;
            float inset = Mathf.Min(5f, span.Length / 4f);
            overshoot = Mathf.Min(overshoot, RoomPast(graph, far, near, overshoot));
            // Short of the end of the track (a terminus's bumper), which the Auto Engineer won't go past anyway.
            return graph.LocationByMoving(graph.LocationOrientedToward(far, near), inset - overshoot, false, true);
        }

        // How far the engine can run on past the far end of a platform track, up to `max`: not over a
        // switch beyond it (it would go wherever the switch is set, into a spur say), stopping clear of it.
        private static float RoomPast(Graph graph, Location far, Location near, float max)
        {
            if (max <= 0f)
                return 0f;
            Location start = graph.LocationOrientedToward(far, near).Flipped();
            TrackSegment last = start.segment;
            Vector3 prev = graph.GetPosition(start);
            for (float d = 2f; d <= max + Wyes.Clearance; d += 2f)
            {
                Location at = graph.LocationByMoving(start, d, false, true);
                Vector3 pos = graph.GetPosition(at);
                if ((pos - prev).sqrMagnitude < 0.01f)
                    return Mathf.Min(max, d);
                prev = pos;
                if (at.segment != last)
                {
                    TrackNode node = last.a == at.segment.a || last.a == at.segment.b ? last.a : last.b;
                    if (graph.IsSwitch(node))
                        return Mathf.Clamp(d - 2f - Wyes.Clearance, 0f, max);
                    last = at.segment;
                }
            }
            return max;
        }

        // How far the cars ahead of the first coach reach: the engine and tender when the engine leads,
        // nothing when the coaches lead.
        private static float Overshoot(Graph graph, List<Car> train, Vector3 platform)
        {
            Vector3 endA = graph.GetPosition(train[0].LocationFor(Car.LogicalEnd.A));
            Vector3 endB = graph.GetPosition(train[train.Count - 1].LocationFor(Car.LogicalEnd.B));
            List<Car> fromLeading = Fuel.Flat(endA, platform) <= Fuel.Flat(endB, platform) ? train : Enumerable.Reverse(train).ToList();
            List<Car> ahead = fromLeading.TakeWhile(c => !c.IsPassengerCar()).ToList();
            if (ahead.Count == 0 || ahead.Count == train.Count)
                return 0f;
            return AutoEngineerPlanner.CalculateTotalLength(ahead);
        }

        // The end of the train farthest from a point: the end that comes in last.
        private static Vector3 Trailing(Graph graph, List<Car> train, Vector3 toward)
        {
            Vector3 endA = graph.GetPosition(train[0].LocationFor(Car.LogicalEnd.A));
            Vector3 endB = graph.GetPosition(train[train.Count - 1].LocationFor(Car.LogicalEnd.B));
            return Fuel.Flat(endA, toward) >= Fuel.Flat(endB, toward) ? endA : endB;
        }
    }
}
