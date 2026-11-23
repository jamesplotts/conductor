using System.Collections.Generic;
using System.Linq;
using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using Track;
using UnityEngine;

namespace Conductor
{
    // A passing loop: the track a train stands on between two switches, X and Y, and another way
    // round from Y back to X for the engine to run around it. Either may have other switches along it.
    internal sealed class Loop
    {
        public TrackNode X, Y;
        // Each switch's outward track (away from the loop).
        public TrackSegment XOut, YOut;
        // For pulling a train in: the standing track's first segment out of X and last into Y, how
        // much train it holds clear of both switches, and its route priority (a siding's is lower).
        public TrackSegment AtX, AtY;
        public HashSet<TrackSegment> Track;
        public float Room;
        public int Priority;
        // The way round, from Y to X: each segment with the node it's entered from.
        public List<(TrackNode from, TrackSegment segment)> Around;
        public float AroundLength;

        public Vector3 Center => (X.Position + Y.Position) / 2f;
        public float AroundRoom => AroundLength - 2f * Wyes.Clearance;

        // Halfway round, where the engine stops before going on out past the far switch.
        public Location AroundMiddle(Graph graph)
        {
            float half = AroundLength / 2f, before = 0f;
            foreach ((TrackNode from, TrackSegment segment) in Around)
            {
                float length = segment.GetLength();
                if (before + length >= half)
                    return Wyes.Along(graph, from, segment, half - before);
                before += length;
            }
            return Wyes.Along(graph, Around[0].from, Around[0].segment, 1f);
        }
    }

    // Turning a train too long for a wye's tail: it stands in a passing loop, and the engine turns on
    // its own and runs around it. An observation car on the rear goes through the wye with the engine
    // and is set back on the other end, platform outward, as the game pays its bonus only for that.
    //
    // A turn is a list of steps, run one at a time: "go>where>note" (a waypoint), "couple>where>car>note",
    // "cut>car>loco|train>note" (uncouple a car from its engine side or its train side), "release>note"
    // (handbrakes off, air through) and "replan" (the train has moved into a loop; plan again from there).
    internal static class Turns
    {
        public const char Sep = '>';
        // The game's car type for an observation car.
        private const string Observation = "PBO";
        // How far from a station to look for a passing loop to turn the train in, in meters.
        private const float LoopSearch = 2500f;

        public static string Go(Location where, string note) => "go" + Sep + Graph.LocationToString(where) + Sep + note;
        // A waypoint the train runs through without stopping (it only picks the route).
        public static string Via(Location where, string note) => "via" + Sep + Graph.LocationToString(where) + Sep + note;
        private static string Couple(Location where, Car car, string note) => "couple" + Sep + Graph.LocationToString(where) + Sep + car.id + Sep + note;
        private static string Cut(Car car, bool fromLoco, string note) => "cut" + Sep + car.id + Sep + (fromLoco ? "loco" : "train") + Sep + note;

        // A wye turn, as steps: out onto the main past the switch it goes in by, into that leg, all the
        // way into the tail, back out into the other leg, and out onto the main past that leg's switch,
        // on the side toward a point. Each waypoint leaves the Auto Engineer only one sensible way to it,
        // so it can't reach a leg the short way round through the tail switch and undo the turn.
        public static List<string> WyeSteps(Graph graph, Wye wye, float length, Vector3 exitToward, string what)
        {
            float outward = length + Wyes.Clearance;
            Location outA = Wyes.AlongRoute(graph, wye.MainA, wye.MainAOut, outward);
            Location outB = Wyes.AlongRoute(graph, wye.MainB, wye.MainBOut, outward);
            bool leaveByA = Fuel.Flat(graph.GetPosition(outA), exitToward) < Fuel.Flat(graph.GetPosition(outB), exitToward);
            Location legA = Wyes.AlongLeg(graph, wye.LegA, wye.LegA.Length / 2f);
            Location legB = Wyes.AlongLeg(graph, wye.LegB, wye.LegB.Length / 2f);
            Location inTail = Wyes.AlongRoute(graph, wye.Tail, wye.TailOut, outward);
            return new List<string>
            {
                Go(leaveByA ? outB : outA, $"Turning {what} on the wye at {wye.Name}"),
                Via(leaveByA ? legB : legA, $"Turning {what}: into the wye"),
                Go(inTail, $"Turning {what}: into the wye's tail"),
                Via(leaveByA ? legA : legB, $"Turning {what}: back out of the tail"),
                Go(leaveByA ? outA : outB, $"Turning {what}: out onto the main"),
            };
        }

        // Plans an engine-only turn. needTurn: the engine must be turned (else it only runs around).
        // Returns the steps, or null with why set.
        public static List<string> PlanEngine(BaseLocomotive loco, Wye wye, Vector3 toward, bool needTurn, bool parked, out string why)
        {
            why = null;
            Graph graph = Graph.Shared;
            List<Car> cars = loco.EnumerateCoupled().ToList();
            int group = cars.TakeWhile(IsPower).Count();
            if (!cars.Take(group).Contains(loco))
            {
                cars.Reverse();
                group = cars.TakeWhile(IsPower).Count();
            }
            if (!cars.Take(group).Contains(loco) || group == cars.Count)
            {
                why = "has its engine in the middle of the train, so Conductor can't run around it.";
                return null;
            }
            List<Car> power = cars.Take(group).ToList();
            // From the engine end to the far end.
            List<Car> coaches = cars.Skip(group).ToList();
            float powerLength = AutoEngineerPlanner.CalculateTotalLength(power);

            Loop loop = StandingIn(graph, coaches);
            if (loop == null)
            {
                if (parked)
                {
                    why = "couldn't get its coaches clear in a passing siding to run around them; running on as it is.";
                    return null;
                }
                return Park(graph, loco, cars, power, powerLength, out why);
            }
            // F: the switch on the side the train leaves by. B: the other.
            bool xIsF = Fuel.Flat(loop.X.Position, toward) < Fuel.Flat(loop.Y.Position, toward);
            var f = new Side(loop, xIsF);
            var b = new Side(loop, !xIsF);
            Vector3 enginePos = graph.GetPosition(loco.LocationF);
            Side engineSide = Fuel.Flat(enginePos, f.Node.Position) < Fuel.Flat(enginePos, b.Node.Position) ? f : b;
            Side wyeSide = wye == null ? null : Fuel.Flat(wye.Center, f.Node.Position) < Fuel.Flat(wye.Center, b.Node.Position) ? f : b;
            if (needTurn && wye == null)
            {
                why = "has no wye to turn its engine on; running on as it is.";
                return null;
            }

            // An observation car at the far end, platform outward, must end up at the other end, turned.
            Car obs = coaches.Last();
            bool carryObs = needTurn && engineSide == b && coaches.Count >= 2 && obs.CarType == Observation
                && !obs[obs.EndToLogical(Car.End.R)].IsCoupled;
            float obsLength = carryObs ? AutoEngineerPlanner.CalculateTotalLength(power.Concat(new[] { obs }).ToList()) : powerLength;
            float longest = Mathf.Max(powerLength, obsLength);
            if (loop.AroundRoom < longest)
            {
                why = $"can't run around its train: the way around the passing siding holds {loop.AroundRoom:0} m and the engine needs {longest:0} m; running on as it is.";
                return null;
            }
            if (needTurn && wye.TailRoom < longest)
            {
                why = $"can't turn its engine{(carryObs ? " and observation car" : "")}: the wye's tail at {wye.Name} holds {wye.TailRoom:0} m and needs {longest:0} m; running on as it is.";
                return null;
            }
            if (!f.Room(graph, longest) || !b.Room(graph, longest))
            {
                why = "can't run around its train: there's no room past one end of the passing siding; running on as it is.";
                return null;
            }

            var steps = new List<string> { Cut(power.Last(), false, "Turning: cutting off the engine") };
            if (carryObs)
            {
                steps.AddRange(RunAround(graph, loop, b, f, powerLength, "fetching the observation car"));
                steps.Add(Couple(CouplerAt(graph, obs, f), obs, "Turning: coupling to the observation car"));
                steps.Add(Cut(obs, false, "Turning: cutting off the observation car"));
                steps.AddRange(Route(graph, loop, f, b, wyeSide, wye, needTurn, obsLength, "with the observation car"));
                Car newRear = coaches[0];
                steps.Add(Couple(CouplerAt(graph, newRear, b), newRear, "Turning: setting the observation car on the rear"));
                steps.Add(Cut(obs, true, "Turning: cutting off the engine"));
                steps.AddRange(RunAround(graph, loop, b, f, powerLength, "running around the train"));
                Car newFront = coaches[coaches.Count - 2];
                steps.Add(Couple(CouplerAt(graph, newFront, f), newFront, "Turning: coupling on the front"));
            }
            else
            {
                steps.AddRange(Route(graph, loop, engineSide, f, wyeSide, wye, needTurn, powerLength, "the engine"));
                Car newFront = coaches.Last();
                steps.Add(Couple(CouplerAt(graph, newFront, f), newFront, "Turning: coupling on the front"));
            }
            steps.Add("release" + Sep + "Turning: brakes off");
            return steps;
        }

        // Moves the whole train into a passing loop near it, clear of both switches, and plans again there.
        private static List<string> Park(Graph graph, BaseLocomotive loco, List<Car> cars, List<Car> power, float powerLength, out string why)
        {
            why = null;
            float length = AutoEngineerPlanner.CalculateTotalLength(cars);
            Vector3 at = graph.GetPosition(loco.LocationF);
            List<Loop> choices = LoopsNear(graph, at, LoopSearch).Where(l => l.Room >= length && l.AroundRoom >= powerLength).ToList();
            if (choices.Count == 0)
            {
                why = "is too long for the wye and there's no passing siding near enough to run around it; running on with the engine at the rear.";
                return null;
            }
            // Nearest, and the siding rather than the main.
            Loop best = choices.OrderBy(l => Mathf.Round(Fuel.Flat(l.Center, at) / 100f)).ThenBy(l => l.Priority).First();
            // Pull in to the clear point at the far switch, so the whole train is inside.
            bool xFar = Fuel.Flat(best.X.Position, at) > Fuel.Flat(best.Y.Position, at);
            Location stop = xFar ? Wyes.Along(graph, best.X, best.AtX, Wyes.Clearance + 2f)
                                 : Wyes.Along(graph, best.Y, best.AtY, Wyes.Clearance + 2f);
            return new List<string> { Go(stop, "Turning: pulling into a siding to run around the train"), "replan" };
        }

        // From one end of the loop to the other, turning on the wye on the way when asked.
        private static IEnumerable<string> Route(Graph graph, Loop loop, Side from, Side to, Side wyeSide, Wye wye, bool turn, float length, string what)
        {
            var steps = new List<string>();
            Side side = from;
            if (turn)
            {
                if (side != wyeSide)
                {
                    steps.AddRange(RunAround(graph, loop, side, wyeSide, length, "heading for the wye"));
                    side = wyeSide;
                }
                steps.AddRange(WyeSteps(graph, wye, length, loop.Center, what));
            }
            if (side != to)
                steps.AddRange(RunAround(graph, loop, side, to, length, "running around the train"));
            return steps;
        }

        // Out past one switch, along the other track, and out past the far switch.
        private static IEnumerable<string> RunAround(Graph graph, Loop loop, Side from, Side to, float length, string what)
        {
            yield return Go(from.Beyond(graph, length), "Turning: " + what);
            yield return Via(loop.AroundMiddle(graph), "Turning: " + what);
            yield return Go(to.Beyond(graph, length), "Turning: " + what);
        }

        // One end of a loop.
        private sealed class Side
        {
            public readonly TrackNode Node;
            public readonly TrackSegment Out;

            public Side(Loop loop, bool x)
            {
                Node = x ? loop.X : loop.Y;
                Out = x ? loop.XOut : loop.YOut;
            }

            public Location Beyond(Graph graph, float length) => Wyes.Along(graph, Node, Out, length + Wyes.Clearance);

            // Whether there's room past the switch for this much train before the track ends.
            public bool Room(Graph graph, float length)
            {
                Wyes.Run run = Wyes.Walk(graph, Node, Out);
                return !run.DeadEnd || run.Length >= length + Wyes.Clearance + 3f;
            }

            public override bool Equals(object obj) => obj is Side s && s.Node == Node;
            public override int GetHashCode() => Node.GetHashCode();
            public static bool operator ==(Side a, Side b) => ReferenceEquals(a, b) || (!ReferenceEquals(a, null) && a.Equals(b));
            public static bool operator !=(Side a, Side b) => !(a == b);
        }

        // Where to couple to a car's end facing a side of the loop.
        private static Location CouplerAt(Graph graph, Car car, Side side)
        {
            Vector3 at = side.Node.Position;
            Car.LogicalEnd end = Fuel.Flat(graph.GetPosition(car.LocationFor(Car.LogicalEnd.A)), at) < Fuel.Flat(graph.GetPosition(car.LocationFor(Car.LogicalEnd.B)), at)
                ? Car.LogicalEnd.A : Car.LogicalEnd.B;
            // As Waypoint Queue does: just past the coupler, facing into the car.
            return car.LogicalToEnd(end) == Car.End.F
                ? graph.LocationByMoving(car.LocationF, 0.5f, false, true)
                : graph.LocationByMoving(car.LocationR, -0.5f, false, true).Flipped();
        }

        private static bool IsPower(Car car) => car.CarType.IsLocomotive || car.CarType.IsTender;

        // The passing loop the coaches stand in, if any: from each end of the coaches, out along the
        // track to a switch they're behind (entered from its branch side), and a way round between
        // the two that doesn't use the coaches' track.
        public static Loop StandingIn(Graph graph, List<Car> coaches)
        {
            Car first = coaches.First(), last = coaches.Last();
            Location endFirst = FarEnd(graph, first, last), endLast = FarEnd(graph, last, first);
            var taken = new HashSet<TrackSegment>();
            foreach (Car car in coaches)
            {
                taken.Add(car.LocationF.segment);
                taken.Add(car.LocationR.segment);
            }
            List<Behind> side1 = SwitchesBehind(graph, endFirst, graph.GetPosition(endLast), taken);
            List<Behind> side2 = SwitchesBehind(graph, endLast, graph.GetPosition(endFirst), taken);
            foreach (var pair in side1.SelectMany(x => side2.Select(y => (x, y))).OrderBy(p => p.x.Distance + p.y.Distance))
            {
                var exclude = new HashSet<TrackSegment>(taken);
                exclude.UnionWith(pair.x.Walked);
                exclude.UnionWith(pair.y.Walked);
                Loop loop = Close(graph, pair.x.Node, pair.x.Arrived, pair.y.Node, pair.y.Arrived, exclude);
                if (loop != null)
                    return loop;
            }
            return null;
        }

        // A switch out along the track from the end of a train, with the train on its branch side.
        private sealed class Behind
        {
            public TrackNode Node;
            public TrackSegment Arrived;
            public float Distance;
            public HashSet<TrackSegment> Walked;
        }

        // The end of a car away from another car.
        private static Location FarEnd(Graph graph, Car car, Car other)
        {
            Vector3 away = graph.GetPosition(other.LocationF);
            return Fuel.Flat(graph.GetPosition(car.LocationF), away) >= Fuel.Flat(graph.GetPosition(car.LocationR), away) ? car.LocationF : car.LocationR;
        }

        // Switches out along the track from a train's end (away from its other end), within reach.
        private static List<Behind> SwitchesBehind(Graph graph, Location end, Vector3 otherEnd, HashSet<TrackSegment> taken)
        {
            var found = new List<Behind>();
            TrackSegment segment = end.segment;
            TrackNode toward = Fuel.Flat(segment.a.Position, otherEnd) >= Fuel.Flat(segment.b.Position, otherEnd) ? segment.a : segment.b;
            var walked = new HashSet<TrackSegment>();
            float distance = Fuel.Flat(graph.GetPosition(end), toward.Position);
            for (int guard = 0; guard < 200 && distance < 1500f; guard++)
            {
                walked.Add(segment);
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(toward);
                if (connected.Count == 1)
                    break;
                TrackSegment next;
                if (graph.IsSwitch(toward) && graph.DecodeSwitchAt(toward, out TrackSegment enter, out _, out _))
                {
                    if (segment != enter)
                    {
                        // Clear of the switch, or the engine can't get past the coaches.
                        if (distance >= Wyes.Clearance - 2f)
                            found.Add(new Behind { Node = toward, Arrived = segment, Distance = distance, Walked = new HashSet<TrackSegment>(walked) });
                        next = enter;
                    }
                    else
                    {
                        next = Wyes.OnwardFromPoints(graph, toward, enter);
                    }
                }
                else if (connected.Count == 2)
                {
                    next = connected[0] == segment ? connected[1] : connected[0];
                }
                else
                {
                    break;
                }
                if (next == null || taken.Contains(next) && next != segment)
                    break;
                TrackNode beyond = next.GetOtherNode(toward);
                distance += next.GetLength();
                segment = next;
                toward = beyond;
            }
            return found;
        }

        // A loop from switches X and Y, each with the standing track arriving on its branch side, if
        // there's a way round from Y's other branch to X's other branch that leaves out these segments.
        private static Loop Close(Graph graph, TrackNode x, TrackSegment xArrived, TrackNode y, TrackSegment yArrived, HashSet<TrackSegment> exclude)
        {
            if (x == y || !graph.DecodeSwitchAt(x, out TrackSegment xEnter, out TrackSegment xa, out TrackSegment xb)
                || !graph.DecodeSwitchAt(y, out TrackSegment yEnter, out TrackSegment ya, out TrackSegment yb))
                return null;
            TrackSegment xOther = xArrived == xa ? xb : xArrived == xb ? xa : null;
            TrackSegment yOther = yArrived == ya ? yb : yArrived == yb ? ya : null;
            if (xOther == null || yOther == null || !xEnter.GroupEnabled || !yEnter.GroupEnabled || exclude.Contains(xOther) || exclude.Contains(yOther))
                return null;
            List<(TrackNode, TrackSegment)> around = WayRound(graph, y, yOther, x, xOther, exclude, out float length);
            if (around == null)
                return null;
            return new Loop { X = x, Y = y, XOut = xEnter, YOut = yEnter, Around = around, AroundLength = length };
        }

        // The shortest way along the track from a node out along a segment to another node, arriving
        // by a given segment, through switches as a train can run, leaving out some segments.
        public static List<(TrackNode from, TrackSegment segment)> WayRound(Graph graph, TrackNode from, TrackSegment first, TrackNode to, TrackSegment arriving, HashSet<TrackSegment> exclude, out float length)
        {
            length = 0f;
            const float limit = 3000f;
            var best = new Dictionary<(TrackSegment, TrackNode), float>();
            var parent = new Dictionary<(TrackSegment, TrackNode), (TrackSegment, TrackNode)?>();
            var open = new List<(TrackSegment segment, TrackNode entered, float length)> { (first, from, first.GetLength()) };
            best[(first, from)] = first.GetLength();
            parent[(first, from)] = null;
            while (open.Count > 0)
            {
                int i = 0;
                for (int j = 1; j < open.Count; j++)
                {
                    if (open[j].length < open[i].length)
                        i = j;
                }
                var (segment, entered, so) = open[i];
                open.RemoveAt(i);
                if (so > best[(segment, entered)] || so > limit)
                    continue;
                TrackNode node = segment.GetOtherNode(entered);
                if (node == to && segment == arriving)
                {
                    var path = new List<(TrackNode from, TrackSegment segment)>();
                    for ((TrackSegment, TrackNode)? at = (segment, entered); at.HasValue; at = parent[at.Value])
                        path.Add((at.Value.Item2, at.Value.Item1));
                    path.Reverse();
                    // Running straight through (no reversing), any way round leaves the engine facing as
                    // it was, even one through a wye, as it leaves Y and comes into X the same way.
                    length = so;
                    return path;
                }
                var onward = new List<TrackSegment>();
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(node);
                if (graph.IsSwitch(node) && graph.DecodeSwitchAt(node, out TrackSegment enter, out TrackSegment a, out TrackSegment b))
                {
                    if (segment == enter)
                    {
                        onward.Add(a);
                        onward.Add(b);
                    }
                    else
                    {
                        onward.Add(enter);
                    }
                }
                else if (connected.Count == 2)
                {
                    onward.Add(connected[0] == segment ? connected[1] : connected[0]);
                }
                foreach (TrackSegment next in onward)
                {
                    if (next == null || exclude.Contains(next) || !next.GroupEnabled)
                        continue;
                    float total = so + next.GetLength();
                    if (best.TryGetValue((next, node), out float known) && known <= total)
                        continue;
                    best[(next, node)] = total;
                    parent[(next, node)] = (segment, entered);
                    open.Add((next, node, total));
                }
            }
            return null;
        }

        // Passing loops near a point a train could pull into whole: a plain track between two
        // switches (no switches along it) and a way round, nearest first.
        public static List<Loop> LoopsNear(Graph graph, Vector3 at, float within)
        {
            var loops = new List<Loop>();
            var seen = new HashSet<TrackSegment>();
            foreach (TrackNode x in graph.Nodes.ToList())
            {
                if (Fuel.Flat(x.Position, at) > within || !graph.IsSwitch(x) || !graph.DecodeSwitchAt(x, out _, out TrackSegment xa, out TrackSegment xb))
                    continue;
                foreach (TrackSegment branch in new[] { xa, xb })
                {
                    if (branch == null || !branch.GroupEnabled)
                        continue;
                    Wyes.Run run = Wyes.Walk(graph, x, branch);
                    if (run.DeadEnd || run.End == null || run.End == x || !graph.IsSwitch(run.End))
                        continue;
                    // Each standing track once, whichever end it's found from.
                    if (seen.Contains(branch) || seen.Contains(run.Last))
                        continue;
                    var track = Segments(graph, x, branch);
                    Loop loop = Close(graph, x, branch, run.End, run.Last, track);
                    if (loop == null)
                        continue;
                    seen.Add(branch);
                    seen.Add(run.Last);
                    loop.AtX = branch;
                    loop.Track = track;
                    loop.AtY = run.Last;
                    loop.Room = run.Length - 2f * Wyes.Clearance;
                    loop.Priority = branch.priority;
                    loops.Add(loop);
                }
            }
            return loops.OrderBy(l => Fuel.Flat(l.Center, at)).ToList();
        }

        // The segments of a plain run of track from a switch to the next.
        private static HashSet<TrackSegment> Segments(Graph graph, TrackNode from, TrackSegment first)
        {
            var segments = new HashSet<TrackSegment>();
            TrackNode at = from;
            TrackSegment segment = first;
            for (int guard = 0; guard < 500 && segment != null && segments.Add(segment); guard++)
            {
                TrackNode next = segment.GetOtherNode(at);
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(next);
                if (connected.Count != 2)
                    break;
                segment = connected[0] == segment ? connected[1] : connected[0];
                at = next;
            }
            return segments;
        }

        // Uncouples a car from its neighbor on the engine side (fromLoco) or the train side, and sets
        // the handbrakes on the cars left standing. Returns the car it was cut from, or null.
        public static Car CutCar(BaseLocomotive loco, string carId, bool fromLoco)
        {
            List<Car> train = loco.EnumerateCoupled().ToList();
            Car car = train.FirstOrDefault(c => c.id == carId);
            if (car == null || !StateManager.IsHost)
                return null;
            int i = train.IndexOf(car), l = train.IndexOf(loco);
            // The neighbor toward the engine, or away from it.
            int toward = l < i ? i - 1 : i + 1;
            int away = l < i ? i + 1 : i - 1;
            int n = fromLoco ? toward : away;
            if (n < 0 || n >= train.Count)
                return null;
            Car other = train[n];
            Car.LogicalEnd end = car.CoupledTo(Car.LogicalEnd.A) == other ? Car.LogicalEnd.A : Car.LogicalEnd.B;
            Car.LogicalEnd otherEnd = other.CoupledTo(Car.LogicalEnd.A) == car ? Car.LogicalEnd.A : Car.LogicalEnd.B;
            // As Waypoint Queue does: close the angle cocks, part the hoses, pull the cut lever.
            car.ApplyEndGearChange(end, Car.EndGearStateKey.Anglecock, 0f);
            car.ApplyEndGearChange(end, Car.EndGearStateKey.IsAirConnected, false);
            other.ApplyEndGearChange(otherEnd, Car.EndGearStateKey.CutLever, 1f);
            other.ApplyEndGearChange(otherEnd, Car.EndGearStateKey.Anglecock, 0f);
            other.ApplyEndGearChange(otherEnd, Car.EndGearStateKey.IsAirConnected, false);
            int cut = Mathf.Max(i, n);
            List<Car> left = l < cut ? train.Skip(cut).ToList() : train.Take(cut).ToList();
            foreach (Car c in left.Where(c => !IsPower(c)))
                c.SetHandbrake(true);
            UpdateCars(loco);
            return other;
        }

        // Air through the whole train and handbrakes off.
        public static void Release(BaseLocomotive loco)
        {
            foreach (Car car in loco.EnumerateCoupled().ToList())
            {
                ConnectAir(car);
                if (!IsPower(car) && car.air.handbrakeApplied)
                    car.SetHandbrake(false);
            }
            UpdateCars(loco);
        }

        // Hoses joined and angle cocks open to the cars either side; closed at an open end.
        public static void ConnectAir(Car car)
        {
            foreach (Car.LogicalEnd end in new[] { Car.LogicalEnd.A, Car.LogicalEnd.B })
            {
                if (car.TryGetAdjacentCar(end, out Car adjacent))
                {
                    Car.LogicalEnd facing = adjacent.CoupledTo(Car.LogicalEnd.A) == car ? Car.LogicalEnd.A : Car.LogicalEnd.B;
                    adjacent.ApplyEndGearChange(facing, Car.EndGearStateKey.IsAirConnected, true);
                    adjacent.ApplyEndGearChange(facing, Car.EndGearStateKey.Anglecock, 1f);
                    car.ApplyEndGearChange(end, Car.EndGearStateKey.IsAirConnected, true);
                    car.ApplyEndGearChange(end, Car.EndGearStateKey.Anglecock, 1f);
                }
                else
                {
                    car.ApplyEndGearChange(end, Car.EndGearStateKey.Anglecock, 0f);
                }
            }
        }

        // Tells the Auto Engineer its train has changed.
        public static void UpdateCars(BaseLocomotive loco)
        {
            var update = AccessTools.Method(typeof(AutoEngineerPlanner), "UpdateCars");
            if (update != null && loco.AutoEngineerPlanner != null)
                update.Invoke(loco.AutoEngineerPlanner, new object[1]);
        }
    }
}
