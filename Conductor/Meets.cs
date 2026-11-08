using System.Collections.Generic;
using System.Linq;
using Game.Messages;
using HarmonyLib;
using Model;
using Model.AI;
using Track;
using Track.Search;
using UI.EngineControls;
using UnityEngine;

namespace Conductor
{
    // A train coming the other way, and where to wait for it.
    internal sealed class Meet
    {
        public BaseLocomotive Other;
        // Where to stop, in a passing siding: the far clear point of the track to wait on. Null to
        // stop where the train is (it's in the siding already).
        public Location? Spot;
        // Every segment of the passing siding, both tracks: the other train has passed once it's
        // off them and its way on doesn't come back over them.
        public HashSet<TrackSegment> Siding;
        public string Where;
        // Waiting on the siding (rather than the main).
        public bool OnSiding;
    }

    // Meets: a Conductor train looks a few miles down its route for a train coming the other way and
    // waits for it in a passing siding, on whichever track the other train's route doesn't take.
    internal static class Meets
    {
        // How far ahead to look, in meters (about three miles).
        private const float LookAhead = 5000f;
        // How far to follow a train's way on when it has no route to read, in meters.
        private const float FollowAhead = 3000f;
        // How much sooner than the other train this one must be in the siding, in seconds.
        private const float Margin = 60f;

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, List<RouteSearch.Step>> RouteField =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, List<RouteSearch.Step>>("_route");
        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, int> StartField =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, int>("_startStepIndex");

        // One segment of a train's way on, the node it leaves the segment by (null when it stops on
        // it), and how far along the way it begins.
        internal struct Leg
        {
            public TrackSegment Segment;
            public TrackNode Exit;
            public float At;
        }

        // Looks for a train coming the other way and somewhere to meet it. Null when there's none,
        // or when there's no siding to meet it in (then why says so).
        public static Meet Find(BaseLocomotive loco, float mph, out BaseLocomotive oncoming, out string why)
        {
            why = null;
            oncoming = null;
            Graph graph = Graph.Shared;
            List<Leg> ours = RouteOf(loco, LookAhead);
            if (ours == null || ours.Count == 0)
                return null;
            List<Car> train = loco.EnumerateCoupled().ToList();
            var mine = new HashSet<Car>(train);
            var seen = new HashSet<Car>(mine);
            foreach (BaseLocomotive other in TrainController.Shared.Cars.OfType<BaseLocomotive>().ToList())
            {
                if (seen.Contains(other) || other.IsInBardo)
                    continue;
                List<Car> theirs = other.EnumerateCoupled().ToList();
                seen.UnionWith(theirs);
                // A Conductor train only goes in the siding when the other one isn't already.
                var theirPlan = new Plan(other);
                if (theirPlan.Running && theirPlan.Enabled && (theirPlan.Phase == Phase.Meeting || string.CompareOrdinal(loco.id, other.id) > 0))
                    continue;
                List<Leg> path = PathOf(other, theirs);
                if (path == null || !Opposing(ours, path))
                    continue;
                oncoming = other;
                return Choose(graph, loco, train, ours, other, path, mph, out why);
            }
            return null;
        }

        // Whether a train coming the other way has gone by a siding: it's off the siding, and its way
        // on doesn't cross it.
        public static bool Passed(BaseLocomotive other, HashSet<TrackSegment> siding)
        {
            if (other == null || other.IsInBardo)
                return true;
            List<Car> theirs = other.EnumerateCoupled().ToList();
            if (theirs.Any(c => siding.Contains(c.LocationF.segment) || siding.Contains(c.LocationR.segment)))
                return false;
            List<Leg> path = PathOf(other, theirs);
            return path == null || !path.Any(l => siding.Contains(l.Segment));
        }

        // Where to wait: the farthest passing siding on this train's route that it can be into before
        // the other train gets there, and that the other train's route goes through.
        private static Meet Choose(Graph graph, BaseLocomotive loco, List<Car> train, List<Leg> ours, BaseLocomotive other, List<Leg> theirs, float mph, out string why)
        {
            why = null;
            float length = AutoEngineerPlanner.CalculateTotalLength(train);
            var theirSegments = new Dictionary<TrackSegment, float>();
            foreach (Leg leg in theirs)
            {
                if (!theirSegments.ContainsKey(leg.Segment))
                    theirSegments[leg.Segment] = leg.At;
            }
            float ourSpeed = Mathf.Max(3f, mph * 0.447f * 0.8f);
            float theirSpeed = Speed(other);

            // Already standing in a siding the other train runs around: wait here.
            if (loco.VelocityMphAbs < 5f)
            {
                Loop here = Turns.StandingIn(graph, train);
                if (here != null)
                {
                    var standing = new HashSet<TrackSegment>(train.SelectMany(c => new[] { c.LocationF.segment, c.LocationR.segment }));
                    bool theyGoRound = here.Around.Any(a => theirSegments.ContainsKey(a.segment));
                    bool theyHitUs = standing.Any(theirSegments.ContainsKey);
                    if (theyGoRound && !theyHitUs)
                    {
                        var siding = new HashSet<TrackSegment>(standing);
                        siding.UnionWith(here.Around.Select(a => a.segment));
                        return new Meet { Other = other, Spot = null, Siding = siding, Where = Near(here.Center), OnSiding = true };
                    }
                }
            }

            var options = new List<(Meet meet, float at, bool inTime)>();
            foreach (Passing p in PassingSidings(graph, ours))
            {
                bool theyTakeOurs = p.Track.Any(theirSegments.ContainsKey);
                bool theyTakeOther = p.Around.Any(a => theirSegments.ContainsKey(a.segment));
                if (theyTakeOurs == theyTakeOther)
                    continue;
                // Wait on whichever track they don't take.
                bool waitOnOther = theyTakeOurs;
                float room = (waitOnOther ? p.AroundLength : p.TrackLength) - 2f * Wyes.Clearance;
                if (room < length)
                    continue;
                Location spot = waitOnOther
                    ? Wyes.Along(graph, p.Y, p.YOther, Wyes.Clearance + 2f)
                    : Wyes.Along(graph, p.Y, p.YArrived, Wyes.Clearance + 2f);
                // In time: this train in and stopped before they reach the siding.
                float theirsAt = p.Track.Concat(p.Around.Select(a => a.segment)).Where(theirSegments.ContainsKey).Select(s => theirSegments[s]).DefaultIfEmpty(0f).Min();
                bool inTime = p.YAt / ourSpeed + Margin < theirsAt / theirSpeed;
                var siding = new HashSet<TrackSegment>(p.Track);
                siding.UnionWith(p.Around.Select(a => a.segment));
                options.Add((new Meet { Other = other, Spot = spot, Siding = siding, Where = Near((p.X.Position + p.Y.Position) / 2f), OnSiding = waitOnOther }, p.YAt, inTime));
            }
            if (options.Count == 0)
            {
                why = "there's no passing siding between them that fits the train";
                return null;
            }
            // The farthest one it can make in time, so the other train waits least; else the nearest.
            var timely = options.Where(o => o.inTime).ToList();
            return timely.Count > 0 ? timely.OrderByDescending(o => o.at).First().meet : options.OrderBy(o => o.at).First().meet;
        }

        // A passing siding on this train's route: in at switch X's points, along Track, out at switch Y
        // from its branch side, with Around the other way from X to Y.
        private sealed class Passing
        {
            public TrackNode X, Y;
            public TrackSegment YArrived, YOther;
            public List<TrackSegment> Track;
            public float TrackLength;
            public List<(TrackNode from, TrackSegment segment)> Around;
            public float AroundLength;
            // How far along the route the train is out past Y.
            public float YAt;
        }

        private static IEnumerable<Passing> PassingSidings(Graph graph, List<Leg> route)
        {
            for (int i = 0; i + 1 < route.Count; i++)
            {
                TrackNode x = route[i].Exit;
                if (x == null || !graph.IsSwitch(x) || !graph.DecodeSwitchAt(x, out TrackSegment xEnter, out TrackSegment xa, out TrackSegment xb) || route[i].Segment != xEnter)
                    continue;
                TrackSegment taken = route[i + 1].Segment;
                TrackSegment xOther = taken == xa ? xb : taken == xb ? xa : null;
                if (xOther == null)
                    continue;
                var track = new List<TrackSegment>();
                float trackLength = 0f;
                for (int j = i + 1; j < route.Count && trackLength < 2500f; j++)
                {
                    track.Add(route[j].Segment);
                    trackLength += route[j].Segment.GetLength();
                    TrackNode y = route[j].Exit;
                    if (y == null || j + 1 >= route.Count || !graph.IsSwitch(y) || !graph.DecodeSwitchAt(y, out TrackSegment yEnter, out TrackSegment ya, out TrackSegment yb))
                        continue;
                    if (route[j + 1].Segment != yEnter || (route[j].Segment != ya && route[j].Segment != yb))
                        continue;
                    TrackSegment yOther = route[j].Segment == ya ? yb : ya;
                    var around = Turns.WayRound(graph, x, xOther, y, yOther, new HashSet<TrackSegment>(track), out float aroundLength);
                    if (around == null || aroundLength > trackLength * 2f + 300f)
                        continue;
                    yield return new Passing
                    {
                        X = x, Y = y, YArrived = route[j].Segment, YOther = yOther,
                        Track = new List<TrackSegment>(track), TrackLength = trackLength,
                        Around = around, AroundLength = aroundLength,
                        YAt = route[j + 1].At,
                    };
                    break;
                }
            }
        }

        // Whether two ways on cross some of the same track in opposite directions.
        private static bool Opposing(List<Leg> ours, List<Leg> theirs)
        {
            var exits = new Dictionary<TrackSegment, TrackNode>();
            foreach (Leg leg in theirs)
            {
                if (leg.Exit != null && !exits.ContainsKey(leg.Segment))
                    exits[leg.Segment] = leg.Exit;
            }
            return ours.Any(l => l.Exit != null && exits.TryGetValue(l.Segment, out TrackNode exit) && exit != l.Exit);
        }

        // A train's way on: its Auto Engineer route when it has one, otherwise along the switches as
        // they lie in the direction it's moving or ordered. Null when it's standing still.
        private static List<Leg> PathOf(BaseLocomotive loco, List<Car> train)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            if (ae.Mode == AutoEngineerMode.Waypoint)
                return ae.Orders.Waypoint.HasValue ? RouteOf(loco, LookAhead) : null;
            float v = loco.velocity;
            if (Mathf.Abs(v) < 0.5f)
            {
                if (ae.Mode != AutoEngineerMode.Road || ae.Orders.MaxSpeedMph == 0)
                    return null;
                v = ae.Orders.Forward ? 1f : -1f;
            }
            Graph graph = Graph.Shared;
            Vector3 heading = (graph.GetPosition(loco.LocationF) - graph.GetPosition(loco.LocationR)) * Mathf.Sign(v);
            Location endA = train[0].LocationFor(Car.LogicalEnd.A);
            Location endB = train[train.Count - 1].LocationFor(Car.LogicalEnd.B);
            Location lead = Vector3.Dot(graph.GetPosition(endA), heading) >= Vector3.Dot(graph.GetPosition(endB), heading) ? endA : endB;
            TrackSegment segment = lead.segment;
            TrackNode toward = Vector3.Dot(segment.a.Position, heading) >= Vector3.Dot(segment.b.Position, heading) ? segment.a : segment.b;
            return Follow(graph, segment, toward, FollowAhead);
        }

        // Along the track from a segment out by one of its nodes, through switches as they lie.
        private static List<Leg> Follow(Graph graph, TrackSegment segment, TrackNode toward, float limit)
        {
            var legs = new List<Leg>();
            float at = 0f;
            for (int guard = 0; guard < 500 && segment != null && at < limit; guard++)
            {
                legs.Add(new Leg { Segment = segment, Exit = toward, At = at });
                at += segment.GetLength();
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(toward);
                TrackSegment next = null;
                if (graph.IsSwitch(toward) && graph.DecodeSwitchAt(toward, out TrackSegment enter, out _, out _))
                {
                    if (segment == enter)
                    {
                        graph.SegmentsReachableFrom(enter, enter.EndForNode(toward), out TrackSegment normal, out TrackSegment reversed);
                        next = toward.isThrown ? reversed : normal;
                    }
                    else
                    {
                        next = enter;
                    }
                }
                else if (connected.Count == 2)
                {
                    next = connected[0] == segment ? connected[1] : connected[0];
                }
                if (next == null)
                    break;
                TrackNode beyond = next.GetOtherNode(toward);
                segment = next;
                toward = beyond;
            }
            return legs;
        }

        // The rest of a train's Auto Engineer route, as far as this.
        private static List<Leg> RouteOf(BaseLocomotive loco, float limit)
        {
            AutoEngineerPlanner planner = loco.AutoEngineerPlanner;
            if (planner == null)
                return null;
            List<RouteSearch.Step> route = RouteField(planner);
            if (route == null || route.Count < 2)
                return null;
            int start = Mathf.Clamp(StartField(planner), 0, route.Count - 1);
            var legs = new List<Leg>();
            float at = 0f;
            for (int i = start + 1; i < route.Count && at < limit; i++)
            {
                TrackSegment segment = route[i].Location.segment;
                if (segment == null)
                    continue;
                legs.Add(new Leg { Segment = segment, Exit = route[i].Node, At = at });
                at += segment.GetLength();
            }
            return legs;
        }

        // How fast a train may come on, in meters a second.
        private static float Speed(BaseLocomotive loco)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            float ordered = ae.Mode != AutoEngineerMode.Off ? ae.Orders.MaxSpeedMph * 0.447f : 0f;
            return Mathf.Max(5f, Mathf.Abs(loco.velocity), ordered);
        }

        private static string Near(Vector3 at)
        {
            var stop = Wyes.NearestStop(at);
            return stop != null ? stop.DisplayName : "the siding";
        }
    }
}
