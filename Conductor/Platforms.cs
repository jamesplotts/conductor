using System.Collections.Generic;
using System.Linq;
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
                    return InSiding(graph, sidings[0].span, sidings[0].siding, train, length, Overshoot(graph, train, sidings[0].span.GetCenterPoint()));
                }
            }
            TrackSpan longest = spans.OrderByDescending(s => s.Length).First();
            return FarEnd(graph, longest, Trailing(graph, train, longest.GetCenterPoint()), Overshoot(graph, train, longest.GetCenterPoint()));
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
        // near switch, otherwise up to the far switch's clearance point.
        private static Location InSiding(Graph graph, TrackSpan span, SidingInfo siding, List<Car> train, float length, float overshoot)
        {
            Vector3 trailing = Trailing(graph, train, span.GetCenterPoint());
            bool aIsFar = Fuel.Flat(siding.EndA.Position, trailing) >= Fuel.Flat(siding.EndB.Position, trailing);
            Location farClear = aIsFar ? Wyes.Along(graph, siding.EndA, siding.LastA, Wyes.Clearance) : Wyes.Along(graph, siding.EndB, siding.LastB, Wyes.Clearance);
            Location nearClear = aIsFar ? Wyes.Along(graph, siding.EndB, siding.LastB, Wyes.Clearance) : Wyes.Along(graph, siding.EndA, siding.LastA, Wyes.Clearance);
            Location platformEnd = FarEnd(graph, span, trailing, overshoot);
            Vector3 near = graph.GetPosition(nearClear);
            // Not past the far switch's clear point, however far the engine runs on.
            if (Fuel.Flat(near, graph.GetPosition(platformEnd)) > Fuel.Flat(near, graph.GetPosition(farClear)))
                return farClear;
            return Fuel.Flat(near, graph.GetPosition(platformEnd)) >= length ? platformEnd : farClear;
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
            // Short of the end of the track (a terminus's bumper), which the Auto Engineer won't go past anyway.
            return graph.LocationByMoving(graph.LocationOrientedToward(far, near), inset - overshoot, false, true);
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
