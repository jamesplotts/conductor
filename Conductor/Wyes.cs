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
        // The leg from each main switch to the tail switch.
        public Leg LegA, LegB;
        // How much train fits in the tail clear of the tail switch, in meters.
        public float TailRoom;
        public string Name;
        // The station the wye belongs to: the one nearest it.
        public string StopId;

        public Vector3 Center => (Tail.Position + MainA.Position + MainB.Position) / 3f;
    }

    // A way along the track between two switches, without reversing: each segment with the node
    // it's entered from.
    internal sealed class Leg
    {
        public List<(TrackNode from, TrackSegment segment)> Steps;
        public float Length;

        public TrackNode Start => Steps[0].from;
        public TrackSegment Last => Steps[Steps.Count - 1].segment;
        public TrackNode End => Last.GetOtherNode(Steps[Steps.Count - 1].from);

        public Leg Reversed()
        {
            var steps = new List<(TrackNode, TrackSegment)>();
            for (int i = Steps.Count - 1; i >= 0; i--)
                steps.Add((Steps[i].segment.GetOtherNode(Steps[i].from), Steps[i].segment));
            return new Leg { Steps = steps, Length = Length };
        }
    }

    internal static class Wyes
    {
        // Keep this far from a switch's points so nothing fouls it, in meters: 100 feet. Any closer
        // and a car standing there blocks a train going through the switch the other way.
        public const float Clearance = 30.5f;

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
            var cache = new Dictionary<(TrackNode, TrackSegment), List<Leg>>();
            List<Leg> LegsFrom(TrackNode node, TrackSegment first)
            {
                if (!cache.TryGetValue((node, first), out List<Leg> legs))
                    cache[(node, first)] = legs = Legs(graph, node, first);
                return legs;
            }
            foreach (TrackNode s in graph.Nodes.ToList())
            {
                if (!graph.IsSwitch(s) || !graph.DecodeSwitchAt(s, out TrackSegment sEnter, out TrackSegment sa, out TrackSegment sb))
                    continue;
                // The other two corners: reached by s's two diverging routes, each coming in on one of
                // its own diverging routes, and joined to each other by their other diverging routes.
                // Legs can run through other switches on the way, as the Sylva yard wye's do.
                Leg found = null, foundX = null, foundY = null;
                foreach (Leg toX in LegsFrom(s, sa))
                {
                    foreach (Leg toY in LegsFrom(s, sb))
                    {
                        if (toX.End == toY.End || toX.End == s || toY.End == s)
                            continue;
                        graph.DecodeSwitchAt(toX.End, out _, out TrackSegment xa0, out TrackSegment xb0);
                        TrackSegment xOther0 = toX.Last == xa0 ? xb0 : xa0;
                        Leg xToY = LegsFrom(toX.End, xOther0).Where(l => l.End == toY.End && l.Last != toY.Last).OrderBy(l => l.Length).FirstOrDefault();
                        if (xToY == null)
                            continue;
                        float total = toX.Length + toY.Length + xToY.Length;
                        if (found == null || total < foundX.Length + foundY.Length + found.Length)
                        {
                            found = xToY;
                            foundX = toX;
                            foundY = toY;
                        }
                    }
                }
                if (found == null)
                    continue;
                TrackNode xNode = foundX.End, yNode = foundY.End;
                string key = string.Join("|", new[] { s.id, xNode.id, yNode.id }.OrderBy(i => i));
                if (!seen.Add(key))
                    continue;
                graph.DecodeSwitchAt(xNode, out TrackSegment xEnter, out _, out _);
                graph.DecodeSwitchAt(yNode, out TrackSegment yEnter, out _, out _);
                var legs = new List<Leg> { foundX, foundY, found };
                // Each switch's outward track. The tail is the one that dead-ends soonest, followed on
                // through switches the main way; the main line runs on for miles both ways. (The
                // nearest switch doesn't tell them apart: at Whittier the main has one 72 m out, at the
                // depot, while the tail runs 3 km up toward Connelly Creek.) Failing a dead end, the
                // one with the least room before a switch.
                var corners = new List<(TrackNode node, TrackSegment outward, float room)>
                {
                    (s, sEnter, Room(graph, s, sEnter)),
                    (xNode, xEnter, Room(graph, xNode, xEnter)),
                    (yNode, yEnter, Room(graph, yNode, yEnter)),
                };
                var ends = corners.Select(c => (c, reach: Reach(graph, c.node, c.outward, TailSearch, out bool ends), ends)).ToList();
                var tail = ends.Any(e => e.ends) ? ends.Where(e => e.ends).OrderBy(e => e.reach).First().c : corners.OrderBy(c => c.room).First();
                // The tail may run on through switches (Whittier's goes on up the Connelly Creek branch):
                // a train can back up it as far as the track goes the main way.
                float reach = Reach(graph, tail.node, tail.outward, TailLimit + Clearance + 3f, out bool deadEnd);
                tail.room = Mathf.Min(TailLimit, reach - Clearance - (deadEnd ? 3f : 0f));
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
                wye.LegA = Between(legs, wye.MainA, wye.Tail);
                wye.LegB = Between(legs, wye.MainB, wye.Tail);
                if (wye.LegA == null || wye.LegB == null)
                    continue;
                PassengerStop nearest = NearestStop(wye.Center);
                wye.Name = nearest != null ? nearest.DisplayName : "an unnamed spot";
                wye.StopId = nearest?.identifier;
                result.Add(wye);
            }
            foreach (Wye wye in result)
                ModEntry.Log?.Info($"Conductor: wye near {wye.Name}: main switches {wye.MainA.id} and {wye.MainB.id}, tail switch {wye.Tail.id}; tail holds {wye.TailRoom:0} m of train.");
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

        // How far a turning train will back up a tail, at most, in meters.
        private const float TailLimit = 800f;
        // The longest wye leg looked for, in meters.
        private const float LegLimit = 900f;
        // How far to follow each corner's track looking for the tail's dead end, in meters.
        private const float TailSearch = 6000f;

        // The way on from a switch entered at its points: the route with the higher priority, or the
        // normal one.
        public static TrackSegment OnwardFromPoints(Graph graph, TrackNode node, TrackSegment enter)
        {
            graph.SegmentsReachableFrom(enter, enter.EndForNode(node), out TrackSegment normal, out TrackSegment reversed);
            if (normal == null || reversed == null)
                return normal ?? reversed;
            return reversed.priority > normal.priority ? reversed : normal;
        }

        // Follows the track from a node along a segment, on through switches the main way, until it
        // ends or this far. Calls visit with each segment, the node it's entered from, and the distance
        // along the way to that node.
        private static float Follow(Graph graph, TrackNode from, TrackSegment first, float limit, out bool deadEnd, System.Func<TrackNode, TrackSegment, float, bool> visit = null)
        {
            deadEnd = false;
            float length = 0f;
            TrackNode at = from;
            TrackSegment segment = first;
            for (int guard = 0; guard < 500 && segment != null && length < limit; guard++)
            {
                if (visit != null && visit(at, segment, length))
                    return length;
                TrackNode next = segment.GetOtherNode(at);
                length += segment.GetLength();
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(next);
                if (connected.Count == 1)
                {
                    deadEnd = true;
                    return length;
                }
                if (graph.IsSwitch(next) && graph.DecodeSwitchAt(next, out TrackSegment enter, out _, out _))
                    segment = segment == enter ? OnwardFromPoints(graph, next, enter) : enter;
                else if (connected.Count == 2)
                    segment = connected[0] == segment ? connected[1] : connected[0];
                else
                    return length;
                at = next;
            }
            return length;
        }

        private static float Reach(Graph graph, TrackNode from, TrackSegment first, float limit, out bool deadEnd) =>
            Follow(graph, from, first, limit, out deadEnd);

        // A point this far out from a node, following the track on through switches the main way.
        public static Location AlongRoute(Graph graph, TrackNode node, TrackSegment segment, float distance)
        {
            Location? found = null;
            Follow(graph, node, segment, distance + 1f, out _, (at, seg, before) =>
            {
                if (before + seg.GetLength() < distance)
                    return false;
                found = Along(graph, at, seg, distance - before);
                return true;
            });
            return found ?? Along(graph, node, segment, distance);
        }

        // Room on a switch's outward track before the end of the track or the next switch.
        private static float Room(Graph graph, TrackNode node, TrackSegment outward)
        {
            Run run = Walk(graph, node, outward);
            return run.Length - Clearance - (run.DeadEnd ? 3f : Clearance);
        }

        // The ways from a switch along one of its routes to other switches, each coming in on one of
        // that switch's diverging routes, without reversing: through plain joints, and on through any
        // switch in between whichever way it can be set. Up to LegLimit long.
        private static List<Leg> Legs(Graph graph, TrackNode from, TrackSegment first)
        {
            var result = new List<Leg>();
            var stack = new Stack<Leg>();
            stack.Push(new Leg { Steps = new List<(TrackNode, TrackSegment)> { (from, first) }, Length = 0f });
            // A big yard has a great many ways through it; a wye's legs are among the first few.
            for (int guard = 0; stack.Count > 0 && guard < 4000; guard++)
            {
                Leg leg = stack.Pop();
                (TrackNode at, TrackSegment segment) = leg.Steps[leg.Steps.Count - 1];
                float length = leg.Length + segment.GetLength();
                if (length > LegLimit)
                    continue;
                TrackNode next = segment.GetOtherNode(at);
                if (next == from || leg.Steps.Any(st => st.from == next))
                    continue;
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(next);
                var onward = new List<TrackSegment>();
                if (connected.Count == 2)
                    onward.Add(connected[0] == segment ? connected[1] : connected[0]);
                else if (graph.IsSwitch(next) && graph.DecodeSwitchAt(next, out TrackSegment enter, out TrackSegment a, out TrackSegment b))
                {
                    if (segment == enter)
                    {
                        onward.Add(a);
                        onward.Add(b);
                    }
                    else
                    {
                        result.Add(new Leg { Steps = leg.Steps, Length = length });
                        onward.Add(enter);
                    }
                }
                foreach (TrackSegment o in onward)
                {
                    if (!OnOpenTrack(o))
                        continue;
                    var steps = new List<(TrackNode, TrackSegment)>(leg.Steps) { (next, o) };
                    stack.Push(new Leg { Steps = steps, Length = length });
                }
            }
            return result;
        }

        // The leg joining two corners, running from the first.
        private static Leg Between(List<Leg> legs, TrackNode from, TrackNode to)
        {
            foreach (Leg leg in legs)
            {
                if (leg.Start == from && leg.End == to)
                    return leg;
                if (leg.Start == to && leg.End == from)
                    return leg.Reversed();
            }
            return null;
        }

        // A point this far along a leg.
        public static Location AlongLeg(Graph graph, Leg leg, float distance)
        {
            float before = 0f;
            foreach ((TrackNode from, TrackSegment segment) in leg.Steps)
            {
                float length = segment.GetLength();
                if (before + length >= distance)
                    return Along(graph, from, segment, distance - before);
                before += length;
            }
            (TrackNode lastFrom, TrackSegment last) = leg.Steps[leg.Steps.Count - 1];
            return Along(graph, lastFrom, last, last.GetLength() / 2f);
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
