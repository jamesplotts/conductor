using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Core;
using Game;
using Game.Messages;
using Game.State;
using Model;
using Model.AI;
using Model.Ops;
using Model.Definition.Data;
using Model.Ops.Definition;
using Network;
using RollingStock;
using Track;
using UI.EngineControls;
using UnityEngine;

namespace Conductor
{
    // Runs every Conductor train, on the host only. Everything is done with Auto Engineer
    // waypoints, so the engine can be sent anywhere: stations, sidings, fuel stops.
    internal sealed class Runner : MonoBehaviour
    {
        // The least a station stop lasts, in game seconds: the station boards and alights every few seconds.
        private const double MinDwell = 30;
        // Give up on a fuel stop that has stopped filling for this long, in game seconds.
        private const double FillStall = 40;
        // Lining-up attempts before giving up on a fuel stop.
        private const int MaxTries = 3;
        // Fuel stops this close to one just used are topped up too (a water tank beside a coal chute).
        private const float NearbyFuel = 200f;

        private static Runner _instance;
        // Fill level last seen while filling, per engine, to tell when filling has stalled.
        private readonly Dictionary<string, (float level, double at)> _filling = new Dictionary<string, (float, double)>();

        public static void Install()
        {
            if (_instance != null)
                return;
            var host = new GameObject("Conductor");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<Runner>();
        }

        public static void Remove()
        {
            if (_instance != null)
                Destroy(_instance.gameObject);
            _instance = null;
        }

        public static void Forget() => _instance?._filling.Clear();

        private void Start() => StartCoroutine(Loop());

        private IEnumerator Loop()
        {
            var wait = new WaitForSecondsRealtime(0.5f);
            while (true)
            {
                yield return wait;
                try
                {
                    TickAll();
                }
                catch (Exception ex)
                {
                    ModEntry.Log?.Error("Conductor: " + ex);
                }
            }
        }

        private void TickAll()
        {
            TrainController trains = TrainController.Shared;
            if (trains == null || Graph.Shared == null || !StateManager.IsHost)
                return;
            foreach (BaseLocomotive loco in trains.Cars.OfType<BaseLocomotive>().ToList())
            {
                if (loco.IsInBardo)
                    continue;
                var plan = new Plan(loco);
                if (plan.Running)
                    Tick(loco, plan);
            }
        }

        // Starts a run from wherever the train is: at the nearest station it calls at, heading
        // down the list (or up it, from the last one).
        public static string Begin(BaseLocomotive loco)
        {
            var plan = new Plan(loco);
            List<Station> route = Route(plan);
            if (route.Count < 2)
                return "Pick at least two stations to run between.";
            Vector3 at = Graph.Shared.GetPosition(loco.LocationF);
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < route.Count; i++)
            {
                float d = Fuel.Flat(at, Graph.Shared.GetPosition(Platform(route[i].Stop, loco).GetValueOrDefault(loco.LocationF)));
                if (d < best)
                {
                    best = d;
                    nearest = i;
                }
            }
            plan.Direction = nearest == route.Count - 1 ? -1 : 1;
            plan.Running = true;
            plan.Tries = 0;
            plan.Loader = "";
            if (AtStation(loco, route[nearest].Stop))
                Arrive(loco, plan, route[nearest].Stop);
            else
                Head(loco, plan, route[nearest].Stop);
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is running {route.First().Name} to {route.Last().Name}.");
            return null;
        }

        public static void End(BaseLocomotive loco, string why)
        {
            var plan = new Plan(loco);
            if (plan.Phase == Phase.Fueling)
            {
                CarLoadTargetLoader loader = Fuel.Find(plan.Loader);
                if (loader != null)
                    Fuel.SetLoading(loader, false);
            }
            plan.Running = false;
            plan.Phase = Phase.Idle;
            plan.Status = why ?? "";
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            if (ae.Mode == AutoEngineerMode.Waypoint && ae.Orders.Waypoint.HasValue)
                ae.ClearWaypoint();
        }

        // Turns the train around: it carries on to the station behind it.
        public static void Reverse(BaseLocomotive loco)
        {
            var plan = new Plan(loco);
            plan.Direction = -plan.Direction;
            if (!plan.Running || plan.Phase != Phase.ToStation)
                return;
            List<Station> route = Route(plan);
            int i = route.FindIndex(s => s.Id == plan.Station);
            if (i < 0)
                return;
            int back = i + plan.Direction;
            if (back >= 0 && back < route.Count)
                Head(loco, plan, route[back].Stop);
        }

        // The stations this train calls at, in order down the line, open ones only.
        public static List<Station> Route(Plan plan)
        {
            HashSet<string> stops = plan.Stops;
            return Line.Stations().Where(s => stops.Contains(s.Id) && s.Closed == null).ToList();
        }

        private void Tick(BaseLocomotive loco, Plan plan)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            // Someone took the controls: hand the train over.
            if (ae.Mode != AutoEngineerMode.Waypoint && plan.Phase != Phase.AtStation && plan.Phase != Phase.Fueling)
            {
                End(loco, "Stopped: the Auto Engineer was taken off waypoints.");
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} handed over; the Auto Engineer was taken off waypoints.");
                return;
            }
            List<Station> route = Route(plan);
            if (route.Count < 2)
            {
                End(loco, "Stopped: fewer than two open stations are picked.");
                return;
            }
            switch (plan.Phase)
            {
                case Phase.ToStation:
                    ToStation(loco, plan, ae, route);
                    break;
                case Phase.AtStation:
                    AtStation(loco, plan, route);
                    break;
                case Phase.ToFuel:
                    ToFuel(loco, plan, ae, route);
                    break;
                case Phase.Fueling:
                    Fueling(loco, plan, route);
                    break;
                case Phase.Turning:
                    Turning(loco, plan, ae, route);
                    break;
                default:
                    Head(loco, plan, route[0].Stop);
                    break;
            }
        }

        private static void ToStation(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            Station station = route.FirstOrDefault(s => s.Id == plan.Station);
            if (station == null)
            {
                // The station was unpicked on the way: go on to the next one.
                Head(loco, plan, Next(plan, route, plan.Station));
                return;
            }
            if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                return;
            if (AtStation(loco, station.Stop))
            {
                Arrive(loco, plan, station.Stop);
                return;
            }
            // Stopped short (a switch against it, or a car in the way): try again, then give up.
            if (++plan.Tries > MaxTries)
            {
                End(loco, $"Stopped: couldn't get into {station.Name}. Check the switches and the track ahead.");
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} couldn't get into {station.Name} and has stopped.");
                return;
            }
            Head(loco, plan, station.Stop);
        }

        private static void Arrive(BaseLocomotive loco, Plan plan, PassengerStop stop)
        {
            plan.Phase = Phase.AtStation;
            plan.Station = stop.identifier;
            plan.Since = TimeWeather.Now.TotalSeconds;
            plan.Tries = 0;
            plan.Turned = false;
            List<Station> route = Route(plan);
            // Leaving from an end of the line, the train goes back the other way.
            int i = route.FindIndex(s => s.Id == stop.identifier);
            if (i == 0)
                plan.Direction = 1;
            else if (i == route.Count - 1)
                plan.Direction = -1;
            // Board passengers for the stations ahead.
            SetDestinations(loco, Ahead(plan, route, stop.identifier));
            plan.Status = $"At {stop.DisplayName}";
        }

        private void AtStation(BaseLocomotive loco, Plan plan, List<Station> route)
        {
            PassengerStop stop = route.FirstOrDefault(s => s.Id == plan.Station)?.Stop ?? PassengerStop.FindAll().FirstOrDefault(s => s.identifier == plan.Station);
            if (stop == null)
            {
                Leave(loco, plan, route);
                return;
            }
            double waited = TimeWeather.Now.TotalSeconds - plan.Since;
            List<Car> coaches = loco.EnumerateCoupled().Where(c => c.IsPassengerCar()).ToList();
            int getting = coaches.Sum(c => (c.GetPassengerMarker() ?? PassengerMarker.Empty()).CountPassengersForStop(stop.identifier));
            HashSet<string> ahead = new HashSet<string>(Ahead(plan, route, stop.identifier));
            int waiting = ahead.Sum(stop.GetTotalWaitingForDestination);
            bool full = coaches.All(Full);
            if (getting > 0)
                plan.Status = $"At {stop.DisplayName}: {getting.Pluralize("passenger")} getting off";
            else if (waiting > 0 && !full)
                plan.Status = $"At {stop.DisplayName}: {waiting} boarding";
            else
                plan.Status = $"At {stop.DisplayName}: all aboard";
            if (waited < MinDwell || getting > 0 || (waiting > 0 && !full))
                return;
            Leave(loco, plan, route);
        }

        // Done at a station: refuel first if the engine is low, then on to the next station.
        private void Leave(BaseLocomotive loco, Plan plan, List<Station> route)
        {
            PassengerStop next = Next(plan, route, plan.Station);
            int at = route.FindIndex(s => s.Id == plan.Station);
            if (plan.TurnOnWyes && !plan.Turned && (at == 0 || at == route.Count - 1))
            {
                plan.Turned = true;
                string note = StartTurn(loco, plan, next);
                if (note == null)
                    return;
                if (note.Length > 0)
                    Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} {note}");
            }
            CarLoadTargetLoader fuel = FuelNeeded(loco, plan, null);
            if (fuel != null)
            {
                plan.Station = next.identifier;
                GoFuel(loco, plan, fuel);
                return;
            }
            Head(loco, plan, next);
        }

        // Turns the train on the nearest wye when the engine would otherwise be at the back for the
        // next leg. Returns null when a turn has started, "" when none is needed, or why it can't.
        private static string StartTurn(BaseLocomotive loco, Plan plan, PassengerStop next)
        {
            Graph graph = Graph.Shared;
            Location? platform = Platform(next, loco);
            if (platform == null)
                return "";
            Vector3 toward = graph.GetPosition(platform.Value);
            List<Car> train = loco.EnumerateCoupled().ToList();
            Vector3 endA = graph.GetPosition(train[0].LocationFor(Car.LogicalEnd.A));
            Vector3 endB = graph.GetPosition(train[train.Count - 1].LocationFor(Car.LogicalEnd.B));
            Vector3 front = graph.GetPosition(loco.LocationF), back = graph.GetPosition(loco.LocationR);
            Vector3 farEnd = Fuel.Flat(endA, front) > Fuel.Flat(endB, front) ? endA : endB;
            bool atFront = Fuel.Flat(front, toward) < Fuel.Flat(farEnd, toward);
            bool facing = Fuel.Flat(front, toward) < Fuel.Flat(back, toward);
            if (atFront && facing)
                return "";
            if (atFront != facing)
                return "needs its engine turned on its own, which Conductor can't do yet; running on as it is.";
            float length = AutoEngineerPlanner.CalculateTotalLength(train);
            Vector3 here = graph.GetPosition(loco.LocationF);
            Wye wye = Wyes.All().Where(w => Fuel.Flat(w.Center, here) < 3000f).OrderBy(w => Fuel.Flat(w.Center, here)).FirstOrDefault();
            if (wye == null)
                return "has no wye nearby to turn on; running back with the engine at the rear.";
            if (length > wye.TailRoom)
                return $"is too long for the wye at {wye.Name} ({length:0} m of train, the tail holds {wye.TailRoom:0} m); running back with the engine at the rear.";
            // Leave by the main switch on the side toward the next station; come in by the other.
            float outward = length + Wyes.Clearance;
            Location outA = Wyes.Along(graph, wye.MainA, wye.MainAOut, outward);
            Location outB = Wyes.Along(graph, wye.MainB, wye.MainBOut, outward);
            bool leaveByA = Fuel.Flat(graph.GetPosition(outA), toward) < Fuel.Flat(graph.GetPosition(outB), toward);
            Location inLeg = leaveByA ? Wyes.Along(graph, wye.MainB, wye.LegB, wye.LegBLength / 2f) : Wyes.Along(graph, wye.MainA, wye.LegA, wye.LegALength / 2f);
            Location inTail = Wyes.Along(graph, wye.Tail, wye.TailOut, outward);
            Location outMain = leaveByA ? outA : outB;
            plan.TurnMoves = new[] { inLeg, inTail, outMain }.Select(Graph.LocationToString).ToArray();
            plan.TurnStep = 0;
            plan.Phase = Phase.Turning;
            plan.Since = TimeWeather.Now.TotalSeconds;
            Send(loco, plan, inLeg, TurnMph(plan));
            plan.Status = $"Turning on the wye at {wye.Name}";
            return null;
        }

        // Through the wye's switches and into its tail at yard speed.
        private static int TurnMph(Plan plan) => Math.Min(10, plan.SpeedMph);

        private void Turning(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                return;
            string[] moves = plan.TurnMoves;
            int step = plan.TurnStep + 1;
            if (step < moves.Length)
            {
                plan.TurnStep = step;
                var aeHelper = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
                aeHelper.SetWaypointOrder(new OrderWaypoint(moves[step], null), TurnMph(plan));
                plan.Status = step == 1 ? "Turning: into the wye's tail" : "Turning: out onto the main";
                return;
            }
            plan.TurnMoves = null;
            Leave(loco, plan, route);
        }

        // The fuel stop to use now, if anything's below the refuel level (or, after filling at
        // one stop, anything not full that a stop close by serves).
        private static CarLoadTargetLoader FuelNeeded(BaseLocomotive loco, Plan plan, CarLoadTargetLoader justUsed)
        {
            float below = plan.RefuelBelow / 100f;
            List<string> carried = Fuel.LoadsFor(loco);
            Vector3 at = Graph.Shared.GetPosition(loco.LocationF);
            CarLoadTargetLoader best = null;
            float bestDistance = float.MaxValue;
            List<CarLoadTargetLoader> candidates = plan.AnyFuelStop ? Fuel.Open(carried)
                : plan.FuelStops.Where(f => carried.Contains(f.LoadId)).Select(f => Fuel.Find(f.LoaderId)).Where(l => l != null).ToList();
            foreach (CarLoadTargetLoader loader in candidates)
            {
                if (loader == justUsed)
                    continue;
                float level = Fuel.Level(loco, loader.load.id);
                if (level < 0f)
                    continue;
                bool wanted = justUsed != null
                    ? level < 0.98f && Fuel.Flat(Fuel.Point(loader), Fuel.Point(justUsed)) <= NearbyFuel
                    : level < below;
                if (!wanted)
                    continue;
                float d = Fuel.Flat(Fuel.Point(loader), at);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = loader;
                }
            }
            return best;
        }

        private static void GoFuel(BaseLocomotive loco, Plan plan, CarLoadTargetLoader loader)
        {
            plan.Loader = loader.keyValueObject.RegisteredId;
            plan.Tries = 0;
            plan.Phase = Phase.ToFuel;
            plan.Since = TimeWeather.Now.TotalSeconds;
            SendToFuel(loco, plan, loader);
        }

        private static void SendToFuel(BaseLocomotive loco, Plan plan, CarLoadTargetLoader loader)
        {
            string what = Fuel.Describe(loader.load.id).ToLowerInvariant();
            if (!Fuel.TryApproach(loco, loader, out Location target, out bool clearing, out string why))
            {
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} skipped taking {what}: {why}.");
                Head(loco, plan, StationStop(plan));
                return;
            }
            Send(loco, plan, target);
            plan.Status = clearing ? $"Backing clear to take {what}" : $"Pulling up for {what}";
        }

        private void ToFuel(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            CarLoadTargetLoader loader = Fuel.Find(plan.Loader);
            if (loader == null)
            {
                Head(loco, plan, StationStop(plan));
                return;
            }
            if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                return;
            string what = Fuel.Describe(loader.load.id).ToLowerInvariant();
            if (Fuel.LinedUp(loco, loader))
            {
                if (!Fuel.SetLoading(loader, true))
                {
                    Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} couldn't work the {what} spout; carrying on.");
                    Head(loco, plan, StationStop(plan));
                    return;
                }
                plan.Phase = Phase.Fueling;
                plan.Since = TimeWeather.Now.TotalSeconds;
                _filling.Remove(loco.id);
                plan.Status = $"Taking {what}";
                return;
            }
            if (++plan.Tries > MaxTries)
            {
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} couldn't line up for {what} (off by {Fuel.Miss(loco, loader):0.0} m); carrying on.");
                Head(loco, plan, StationStop(plan));
                return;
            }
            SendToFuel(loco, plan, loader);
        }

        private void Fueling(BaseLocomotive loco, Plan plan, List<Station> route)
        {
            CarLoadTargetLoader loader = Fuel.Find(plan.Loader);
            if (loader == null)
            {
                Head(loco, plan, StationStop(plan));
                return;
            }
            double now = TimeWeather.Now.TotalSeconds;
            float level = Fuel.Level(loco, loader.load.id);
            if (!_filling.TryGetValue(loco.id, out (float level, double at) last) || level > last.level + 0.001f)
                _filling[loco.id] = last = (level, now);
            string what = Fuel.Describe(loader.load.id).ToLowerInvariant();
            plan.Status = $"Taking {what}: {level:P0}";
            bool stalled = now - last.at > FillStall;
            if (level < 0.995f && !stalled)
                return;
            Fuel.SetLoading(loader, false);
            _filling.Remove(loco.id);
            if (stalled && level < 0.995f)
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} stopped taking {what} at {level:P0}; the stop may be empty.");
            CarLoadTargetLoader more = FuelNeeded(loco, plan, loader);
            if (more != null)
            {
                GoFuel(loco, plan, more);
                return;
            }
            plan.Loader = "";
            Head(loco, plan, StationStop(plan));
        }

        // The station a fuel stop was on the way to.
        private static PassengerStop StationStop(Plan plan)
        {
            string id = plan.Station;
            return PassengerStop.FindAll().FirstOrDefault(s => s.identifier == id);
        }

        private static void Head(BaseLocomotive loco, Plan plan, PassengerStop stop)
        {
            if (stop == null)
            {
                End(loco, "Stopped: lost track of the next station.");
                return;
            }
            Location? platform = Platform(stop, loco);
            if (platform == null)
            {
                End(loco, $"Stopped: {stop.DisplayName} has no platform track.");
                return;
            }
            if (plan.Phase != Phase.ToStation || plan.Station != stop.identifier)
                plan.Tries = 0;
            plan.Phase = Phase.ToStation;
            plan.Station = stop.identifier;
            plan.Since = TimeWeather.Now.TotalSeconds;
            Send(loco, plan, platform.Value);
            plan.Status = $"Heading for {stop.DisplayName}";
        }

        private static void Send(BaseLocomotive loco, Plan plan, Location location, int? mph = null)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            ae.SetWaypointOrder(new OrderWaypoint(Graph.LocationToString(location), null), mph ?? plan.SpeedMph);
        }

        // Where to stop at a station: near the far end of its longest platform track, so the
        // whole train pulls in along it.
        public static Location? Platform(PassengerStop stop, BaseLocomotive loco)
        {
            TrackSpan span = stop.TrackSpans.Where(s => s != null && s.lower.HasValue && s.upper.HasValue).OrderByDescending(s => s.Length).FirstOrDefault();
            if (span == null)
                return null;
            Graph graph = Graph.Shared;
            Location lower = span.lower.Value, upper = span.upper.Value;
            Vector3 at = graph.GetPosition(loco.LocationF);
            bool upperFar = Fuel.Flat(graph.GetPosition(upper), at) >= Fuel.Flat(graph.GetPosition(lower), at);
            Location far = upperFar ? upper : lower, near = upperFar ? lower : upper;
            float inset = Mathf.Min(5f, span.Length / 4f);
            return graph.LocationByMoving(graph.LocationOrientedToward(far, near), inset, false, true);
        }

        private static bool AtStation(BaseLocomotive loco, PassengerStop stop)
        {
            var train = new HashSet<Car>(loco.EnumerateCoupled());
            var onSpan = new List<Car>();
            foreach (TrackSpan span in stop.TrackSpans)
            {
                if (span == null)
                    continue;
                onSpan.Clear();
                TrainController.Shared.GetCarsOnSpan(span, onSpan);
                if (onSpan.Any(train.Contains))
                    return true;
            }
            return false;
        }

        // The next station after this one in the direction of travel, turning back at the ends.
        private static PassengerStop Next(Plan plan, List<Station> route, string fromId)
        {
            int i = route.FindIndex(s => s.Id == fromId);
            if (i < 0)
                return route[plan.Direction > 0 ? 0 : route.Count - 1].Stop;
            int n = i + plan.Direction;
            if (n < 0 || n >= route.Count)
            {
                plan.Direction = -plan.Direction;
                n = i + plan.Direction;
            }
            return route[n].Stop;
        }

        // The stations still to come on this leg, from a station, as the train will leave it.
        private static List<string> Ahead(Plan plan, List<Station> route, string fromId)
        {
            int i = route.FindIndex(s => s.Id == fromId);
            if (i < 0)
                return route.Select(s => s.Id).Where(id => id != fromId).ToList();
            int dir = plan.Direction;
            if (i + dir < 0 || i + dir >= route.Count)
                dir = -dir;
            var ids = new List<string>();
            for (int n = i + dir; n >= 0 && n < route.Count; n += dir)
                ids.Add(route[n].Id);
            return ids;
        }

        // Who the coaches take on: passengers for these stations only.
        private static void SetDestinations(BaseLocomotive loco, List<string> stations)
        {
            foreach (Car coach in loco.EnumerateCoupled().Where(c => c.IsPassengerCar()))
            {
                PassengerMarker marker = coach.GetPassengerMarker() ?? PassengerMarker.Empty();
                var wanted = new HashSet<string>(stations);
                if (!marker.AutoDestinationsFromTimetable && marker.Destinations.SetEquals(wanted))
                    continue;
                marker.Destinations = wanted;
                marker.AutoDestinationsFromTimetable = false;
                coach.SetPassengerMarker(marker);
            }
        }

        private static bool Full(Car coach)
        {
            Load passengers = CarLoadLibrary.GetLoad("rr/passengers");
            LoadSlot slot = coach.Definition.LoadSlots.FirstOrDefault(s => s.LoadRequirementsMatch(passengers));
            if (slot == null)
                return true;
            return (coach.GetPassengerMarker() ?? PassengerMarker.Empty()).TotalPassengers >= (int)slot.MaximumCapacity;
        }
    }
}
