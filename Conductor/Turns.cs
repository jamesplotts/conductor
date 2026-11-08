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
    // A passing loop: two tracks side by side between the same two switches.
    internal sealed class Loop
    {
        public TrackNode X, Y;
        // Each switch's outward track (away from the loop).
        public TrackSegment XOut, YOut;
        public LoopTrack[] Tracks;

        public Vector3 Center => (X.Position + Y.Position) / 2f;
        public LoopTrack Other(LoopTrack track) => Tracks[0] == track ? Tracks[1] : Tracks[0];
    }

    internal sealed class LoopTrack
    {
        // The track's first segment out of X and its last segment into Y.
        public TrackSegment AtX, AtY;
        public HashSet<TrackSegment> Segments;
        public float Length;
        public int Priority;
        // Train that fits between the switches, clear of both.
        public float Room => Length - 2f * Wyes.Clearance;
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
        private static string Couple(Location where, Car car, string note) => "couple" + Sep + Graph.LocationToString(where) + Sep + car.id + Sep + note;
        private static string Cut(Car car, bool fromLoco, string note) => "cut" + Sep + car.id + Sep + (fromLoco ? "loco" : "train") + Sep + note;

        // The three waypoints of a wye turn: into the far leg, into the tail, and out onto the main on
        // the side toward a point.
        public static Location[] WyeMoves(Graph graph, Wye wye, float length, Vector3 exitToward)
        {
            float outward = length + Wyes.Clearance;
            Location outA = Wyes.AlongRoute(graph, wye.MainA, wye.MainAOut, outward);
            Location outB = Wyes.AlongRoute(graph, wye.MainB, wye.MainBOut, outward);
            bool leaveByA = Fuel.Flat(graph.GetPosition(outA), exitToward) < Fuel.Flat(graph.GetPosition(outB), exitToward);
            Location inLeg = leaveByA ? Wyes.Along(graph, wye.MainB, wye.LegB, wye.LegBLength / 2f) : Wyes.Along(graph, wye.MainA, wye.LegA, wye.LegALength / 2f);
            Location inTail = Wyes.AlongRoute(graph, wye.Tail, wye.TailOut, outward);
            return new[] { inLeg, inTail, leaveByA ? outA : outB };
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

            Loop loop = null;
            LoopTrack standing = null;
            foreach (Loop l in LoopsNear(graph, Middle(graph, coaches), 600f))
            {
                standing = l.Tracks.FirstOrDefault(t => coaches.All(c => Clear(graph, l, t, c)));
                if (standing != null)
                {
                    loop = l;
                    break;
                }
            }
            if (loop == null)
            {
                if (parked)
                {
                    why = "couldn't get its coaches clear in a passing siding to run around them; running on as it is.";
                    return null;
                }
                return Park(graph, loco, cars, power, powerLength, out why);
            }
            LoopTrack around = loop.Other(standing);

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
            if (around.Room < longest)
            {
                why = $"can't run around its train: the other track of the passing siding holds {around.Room:0} m and the engine needs {longest:0} m; running on as it is.";
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
                steps.AddRange(RunAround(graph, loop, around, b, f, powerLength, "fetching the observation car"));
                steps.Add(Couple(CouplerAt(graph, obs, f), obs, "Turning: coupling to the observation car"));
                steps.Add(Cut(obs, false, "Turning: cutting off the observation car"));
                steps.AddRange(Route(graph, loop, around, f, b, wyeSide, wye, needTurn, obsLength, "with the observation car"));
                Car newRear = coaches[0];
                steps.Add(Couple(CouplerAt(graph, newRear, b), newRear, "Turning: setting the observation car on the rear"));
                steps.Add(Cut(obs, true, "Turning: cutting off the engine"));
                steps.AddRange(RunAround(graph, loop, around, b, f, powerLength, "running around the train"));
                Car newFront = coaches[coaches.Count - 2];
                steps.Add(Couple(CouplerAt(graph, newFront, f), newFront, "Turning: coupling on the front"));
            }
            else
            {
                steps.AddRange(Route(graph, loop, around, engineSide, f, wyeSide, wye, needTurn, powerLength, "the engine"));
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
            var choices = new List<(Loop loop, LoopTrack track, float d)>();
            foreach (Loop l in LoopsNear(graph, at, LoopSearch))
            {
                foreach (LoopTrack t in l.Tracks)
                {
                    if (t.Room >= length && l.Other(t).Room >= powerLength)
                        choices.Add((l, t, Fuel.Flat(l.Center, at)));
                }
            }
            if (choices.Count == 0)
            {
                why = "is too long for the wye and there's no passing siding near enough to run around it; running on with the engine at the rear.";
                return null;
            }
            // Nearest, and the siding rather than the main.
            var best = choices.OrderBy(c => c.d).ThenBy(c => c.track.Priority).First();
            // Pull in to the clear point at the far switch, so the whole train is inside.
            bool xFar = Fuel.Flat(best.loop.X.Position, at) > Fuel.Flat(best.loop.Y.Position, at);
            Location stop = xFar ? Wyes.Along(graph, best.loop.X, best.track.AtX, Wyes.Clearance + 2f)
                                 : Wyes.Along(graph, best.loop.Y, best.track.AtY, Wyes.Clearance + 2f);
            return new List<string> { Go(stop, "Turning: pulling into a siding to run around the train"), "replan" };
        }

        // From one end of the loop to the other, turning on the wye on the way when asked.
        private static IEnumerable<string> Route(Graph graph, Loop loop, LoopTrack around, Side from, Side to, Side wyeSide, Wye wye, bool turn, float length, string what)
        {
            var steps = new List<string>();
            Side side = from;
            if (turn)
            {
                if (side != wyeSide)
                {
                    steps.AddRange(RunAround(graph, loop, around, side, wyeSide, length, "heading for the wye"));
                    side = wyeSide;
                }
                Location[] moves = WyeMoves(graph, wye, length, loop.Center);
                steps.Add(Go(moves[0], $"Turning {what} on the wye at {wye.Name}"));
                steps.Add(Go(moves[1], $"Turning {what}: into the wye's tail"));
                steps.Add(Go(moves[2], $"Turning {what}: back out of the wye"));
            }
            if (side != to)
                steps.AddRange(RunAround(graph, loop, around, side, to, length, "running around the train"));
            return steps;
        }

        // Out past one switch, along the other track, and out past the far switch.
        private static IEnumerable<string> RunAround(Graph graph, Loop loop, LoopTrack around, Side from, Side to, float length, string what)
        {
            yield return Go(from.Beyond(graph, length), "Turning: " + what);
            yield return Go(Wyes.Along(graph, loop.X, around.AtX, around.Length / 2f), "Turning: " + what);
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

        private static Vector3 Middle(Graph graph, List<Car> cars) =>
            (graph.GetPosition(cars.First().LocationF) + graph.GetPosition(cars.Last().LocationR)) / 2f;

        // Whether a car stands on a loop track, clear of both switches.
        private static bool Clear(Graph graph, Loop loop, LoopTrack track, Car car)
        {
            foreach (Location end in new[] { car.LocationF, car.LocationR })
            {
                if (!track.Segments.Contains(end.segment))
                    return false;
                Vector3 p = graph.GetPosition(end);
                if (Fuel.Flat(p, loop.X.Position) < Wyes.Clearance - 2f || Fuel.Flat(p, loop.Y.Position) < Wyes.Clearance - 2f)
                    return false;
            }
            return true;
        }

        // Passing loops on open track near a point, nearest first.
        public static List<Loop> LoopsNear(Graph graph, Vector3 at, float within)
        {
            var loops = new List<Loop>();
            var seen = new HashSet<string>();
            foreach (TrackNode x in graph.Nodes.ToList())
            {
                if (Fuel.Flat(x.Position, at) > within || !graph.IsSwitch(x) || !graph.DecodeSwitchAt(x, out TrackSegment xEnter, out TrackSegment xa, out TrackSegment xb))
                    continue;
                LoopTrack ta = Walk(graph, x, xa, out TrackNode endA);
                LoopTrack tb = Walk(graph, x, xb, out TrackNode endB);
                if (ta == null || tb == null || endA != endB || endA == x || !graph.IsSwitch(endA))
                    continue;
                TrackNode y = endA;
                if (!graph.DecodeSwitchAt(y, out TrackSegment yEnter, out _, out _) || ta.AtY == yEnter || tb.AtY == yEnter)
                    continue;
                if (!xEnter.GroupEnabled || !yEnter.GroupEnabled || !xa.GroupEnabled || !xb.GroupEnabled)
                    continue;
                if (!seen.Add(string.Join("|", new[] { x.id, y.id }.OrderBy(i => i))))
                    continue;
                loops.Add(new Loop { X = x, Y = y, XOut = xEnter, YOut = yEnter, Tracks = new[] { ta, tb } });
            }
            return loops.OrderBy(l => Fuel.Flat(l.Center, at)).ToList();
        }

        // A plain run of track from a switch to the next switch, or null at a dead end.
        private static LoopTrack Walk(Graph graph, TrackNode from, TrackSegment first, out TrackNode end)
        {
            var track = new LoopTrack { AtX = first, Segments = new HashSet<TrackSegment>(), Priority = first.priority };
            end = null;
            TrackNode at = from;
            TrackSegment segment = first;
            for (int guard = 0; guard < 500 && segment != null; guard++)
            {
                TrackNode next = segment.GetOtherNode(at);
                track.Segments.Add(segment);
                track.Length += segment.GetLength();
                track.AtY = segment;
                IReadOnlyList<TrackSegment> connected = graph.SegmentsConnectedTo(next);
                if (connected.Count == 1)
                    return null;
                if (connected.Count != 2 || next == from)
                {
                    end = next;
                    return track;
                }
                segment = connected[0] == segment ? connected[1] : connected[0];
                at = next;
            }
            return null;
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
