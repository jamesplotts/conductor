using System.Collections.Generic;
using System.Linq;
using Model.Ops;
using Track;
using UnityEngine;

namespace Conductor
{
    // A wye: two switches on the main line and one at the tail, joined by three legs. Running a
    // train in by one leg, all the way into the tail, and out by the other leg turns it end for end.
    internal sealed class Wye
    {
        public TrackNode Tail;
        public TrackNode MainA, MainB;
        // The first segment of each switch's outward track (the main line either way, and the tail).
        public TrackSegment TailOut, MainAOut, MainBOut;
        // The first segment of the leg from each main switch to the tail switch, and its length.
        public TrackSegment LegA, LegB;
        public float LegALength, LegBLength;
        // How much train fits in the tail clear of the tail switch, in meters.
        public float TailRoom;
        public string Name;
        // The station the wye belongs to: the one nearest it.
        public string StopId;

        public Vector3 Center => (Tail.Position + MainA.Position + MainB.Position) / 3f;
    }

    internal static class Wyes
    {
        // Keep this far from a switch's points so nothing fouls it, in meters.
        public const float Clearance = 12f;

        private static List<Wye> _found;
        private static float _foundAt;

        public static void Forget() => _found = null;

        // Every wye on open track. Found again every minute, as expansions open.
        public static List<Wye> All()
        {
            if (_found != null && Time.realtimeSinceStartup - _foundAt < 60f)
                return _found;
            _foundAt = Time.realtimeSinceStartup;
            _found = Find();
            return _found;
        }

        private static List<Wye> Find()
        {
            var result = new List<Wye>();
            Graph graph = Graph.Shared;
            if (graph == null)
                return result;
            var seen = new HashSet<string>();
            foreach (TrackNode s in graph.Nodes.ToList())
            {
                if (!graph.IsSwitch(s) || !graph.DecodeSwitchAt(s, out TrackSegment sEnter, out TrackSegment sa, out TrackSegment sb))
                    continue;
                Run toX = Walk(graph, s, sa), toY = Walk(graph, s, sb);
                if (!Diverges(graph, toX) || !Diverges(graph, toY) || toX.End == toY.End)
                    continue;
                // X and Y must be joined by their other diverging routes.
                if (!graph.DecodeSwitchAt(toX.End, out TrackSegment xEnter, out TrackSegment xa, out TrackSegment xb))
                    continue;
                TrackSegment xOther = toX.Last == xa ? xb : xa;
                Run xToY = Walk(graph, toX.End, xOther);
                if (xToY.End != toY.End || !Diverges(graph, xToY))
                    continue;
                string key = string.Join("|", new[] { s.id, toX.End.id, toY.End.id }.OrderBy(i => i));
                if (!seen.Add(key))
                    continue;
                graph.DecodeSwitchAt(toY.End, out TrackSegment yEnter, out _, out _);
                // Each switch's outward track; the tail is the one that ends soonest.
                var corners = new List<(TrackNode node, TrackSegment outward, float room)>
                {
                    (s, sEnter, Room(graph, s, sEnter)),
                    (toX.End, xEnter, Room(graph, toX.End, xEnter)),
                    (toY.End, yEnter, Room(graph, toY.End, yEnter)),
                };
                var tail = corners.OrderBy(c => c.room).First();
                var mains = corners.Where(c => c.node != tail.node).ToList();
                if (!OnOpenTrack(tail.outward) || !OnOpenTrack(mains[0].outward) || !OnOpenTrack(mains[1].outward))
                    continue;
                var wye = new Wye
                {
                    Tail = tail.node,
                    TailOut = tail.outward,
                    TailRoom = tail.room,
                    MainA = mains[0].node,
                    MainAOut = mains[0].outward,
                    MainB = mains[1].node,
                    MainBOut = mains[1].outward,
                };
                (wye.LegA, wye.LegALength) = Leg(graph, wye.MainA, wye.Tail);
                (wye.LegB, wye.LegBLength) = Leg(graph, wye.MainB, wye.Tail);
                if (wye.LegA == null || wye.LegB == null)
                    continue;
                PassengerStop nearest = NearestStop(wye.Center);
                wye.Name = nearest != null ? nearest.DisplayName : "an unnamed spot";
                wye.StopId = nearest?.identifier;
                result.Add(wye);
            }
            foreach (Wye wye in result)
                ModEntry.Log?.Info($"Conductor: wye near {wye.Name}, tail holds {wye.TailRoom:0} m of train.");
            return result;
        }

        // A run of track from a switch, through plain joints, to the next switch or the end of the track.
        internal struct Run
        {
            public TrackNode End;
            public TrackSegment Last;
            public float Length;
            public bool DeadEnd;
        }

        internal static Run Walk(Graph graph, TrackNode from, TrackSegment first)
        {
            var run = new Run();
            TrackNode at = from;
            TrackSegment segment = first;
            for (int guard = 0; guard < 500 && segment != null; guard++)
            {
                TrackNode next = segment.GetOtherNode(at);
                run.Length += segment.GetLength();
                run.Last = segment;
                run.End = next;
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(next);
                if (connected.Count == 1)
                {
                    run.DeadEnd = true;
                    return run;
                }
                if (connected.Count != 2 || next == from)
                    return run;
                segment = connected[0] == segment ? connected[1] : connected[0];
                at = next;
            }
            return run;
        }

        // Whether a run ends at a switch, coming in on one of its diverging routes.
        private static bool Diverges(Graph graph, Run run)
        {
            if (run.End == null || run.DeadEnd || !graph.IsSwitch(run.End))
                return false;
            return graph.DecodeSwitchAt(run.End, out _, out TrackSegment a, out TrackSegment b) && (run.Last == a || run.Last == b);
        }

        // Room on a switch's outward track before the end of the track or the next switch.
        private static float Room(Graph graph, TrackNode node, TrackSegment outward)
        {
            Run run = Walk(graph, node, outward);
            return run.Length - Clearance - (run.DeadEnd ? 3f : Clearance);
        }

        private static (TrackSegment first, float length) Leg(Graph graph, TrackNode from, TrackNode to)
        {
            if (!graph.DecodeSwitchAt(from, out _, out TrackSegment a, out TrackSegment b))
                return (null, 0f);
            foreach (TrackSegment first in new[] { a, b })
            {
                Run run = Walk(graph, from, first);
                if (run.End == to)
                    return (first, run.Length);
            }
            return (null, 0f);
        }

        private static bool OnOpenTrack(TrackSegment segment) => segment != null && segment.GroupEnabled;

        public static PassengerStop NearestStop(Vector3 at) => PassengerStop.FindAll()
            .Where(s => s.TrackSpans.Any(t => t != null))
            .OrderBy(s => Fuel.Flat(s.TrackSpans.First(t => t != null).GetCenterPoint(), at))
            .FirstOrDefault();

        // A point on the track, this far out from a node along a segment and on past it.
        public static Location Along(Graph graph, TrackNode node, TrackSegment segment, float distance)
        {
            Location start = Nearer(segment, node.Position, 0.1f);
            Location probe = Nearer(segment, node.Position, Mathf.Min(2f, segment.GetLength() * 0.5f));
            start = graph.LocationOrientedToward(start, probe);
            return graph.LocationByMoving(start, distance - 0.1f, false, true);
        }

        // A point this far into a segment from whichever of its ends is at the given position.
        private static Location Nearer(TrackSegment segment, Vector3 nodePosition, float distance)
        {
            var fromA = new Location(segment, distance, TrackSegment.End.A);
            var fromB = new Location(segment, distance, TrackSegment.End.B);
            return Vector3.Distance(fromA.GetPosition(), nodePosition) <= Vector3.Distance(fromB.GetPosition(), nodePosition) ? fromA : fromB;
        }
    }
}
