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
        private const int MaxFuelMoves = 6;
        // Aim at the far end of the platform again this close to the station, in meters.
        private const float ReAimMeters = 400f;
        // Fuel stops this close to one just used are topped up too (a water tank beside a coal chute).
        private const float NearbyFuel = 200f;

        private static Runner _instance;
        // Fill level last seen while filling, per engine, to tell when filling has stalled.
        private readonly Dictionary<string, (float level, double at)> _filling = new Dictionary<string, (float, double)>();
        // When each engine last looked ahead for trains coming the other way, in real seconds.
        private readonly Dictionary<string, float> _lookedAhead = new Dictionary<string, float>();
        // Look ahead this often, in real seconds.
        private const float LookEvery = 5f;
        // Give up waiting for a train that doesn't come, in game seconds.
        private const double MeetPatience = 30 * 60;

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

        public static void Forget()
        {
            _instance?._filling.Clear();
            _instance?._lookedAhead.Clear();
        }

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
            // From an end the only way is back; from partway along, the way the train is going.
            plan.Direction = nearest == 0 ? 1 : nearest == route.Count - 1 ? -1 : plan.FirstWay != 0 ? plan.FirstWay : WayGoing(loco, route, nearest);
            plan.Running = true;
            WaypointMode(loco, plan);
            plan.Enabled = true;
            plan.Held = false;
            plan.Tries = 0;
            plan.Loader = "";
            if (AtStation(loco, route[nearest].Stop))
                Arrive(loco, plan, route[nearest].Stop);
            else
                Head(loco, plan, NextFromHere(loco, plan, route));
            string toward = plan.Direction > 0 ? route.Last().Name : route.First().Name;
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} starting near {route[nearest].Name}, going toward {toward}.");
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is running {route.First().Name} to {route.Last().Name}, going toward {toward} first.");
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
            plan.Held = false;
            plan.Phase = Phase.Idle;
            plan.Status = why ?? "";
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            if (ae.Mode == AutoEngineerMode.Waypoint && ae.Orders.Waypoint.HasValue)
                ae.ClearWaypoint();
        }

        // Disabled: stop the train where it is and let the spout go, so a player can sort things
        // out by hand. The run keeps its place for when Conductor is enabled again.
        private void Hold(BaseLocomotive loco, Plan plan)
        {
            plan.Held = true;
            if (plan.Phase == Phase.Fueling)
            {
                CarLoadTargetLoader loader = Fuel.Find(plan.Loader);
                if (loader != null)
                    Fuel.SetLoading(loader, false);
                _filling.Remove(loco.id);
            }
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            if (ae.Mode == AutoEngineerMode.Waypoint && ae.Orders.Waypoint.HasValue)
                ae.ClearWaypoint();
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is disabled and holding; it's yours until Conductor is enabled again.");
        }

        // Enabled again: pick the run up where it left off, from wherever the train is now.
        private void Resume(BaseLocomotive loco, Plan plan)
        {
            plan.Held = false;
            plan.Tries = 0;
            plan.Since = TimeWeather.Now.TotalSeconds;
            WaypointMode(loco, plan);
            List<Station> route = Route(plan);
            if (route.Count < 2)
                return;
            switch (plan.Phase)
            {
                case Phase.ToStation:
                    Head(loco, plan, NextFromHere(loco, plan, route));
                    break;
                case Phase.ToFuel:
                case Phase.Fueling:
                    CarLoadTargetLoader loader = Fuel.Find(plan.Loader);
                    if (loader == null)
                        Head(loco, plan, NextFromHere(loco, plan, route));
                    else if (Fuel.LinedUp(loco, loader) && Fuel.SetLoading(loader, true))
                        plan.Phase = Phase.Fueling;
                    else
                    {
                        plan.Phase = Phase.ToFuel;
                        SendToFuel(loco, plan, loader);
                    }
                    break;
                case Phase.Layover:
                    if (!plan.ParkedUp && plan.ParkSpot.Length > 0)
                        Order(loco, plan, new OrderWaypoint(plan.ParkSpot, null), TurnMph(plan));
                    break;
                case Phase.Meeting:
                    if (!plan.MeetWaiting && plan.MeetSpot.Length > 0)
                        Order(loco, plan, new OrderWaypoint(plan.MeetSpot, null), plan.SpeedMph);
                    break;
                case Phase.Turning:
                    string[] steps = plan.TurnMoves;
                    if (plan.TurnStep < steps.Length && (StepKind(steps[plan.TurnStep]) == "go" || StepKind(steps[plan.TurnStep]) == "couple"))
                        IssueStep(loco, plan, steps[plan.TurnStep]);
                    break;
                // At a station the dwell starts over; leaving sends the train on as usual.
            }
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is enabled again and carrying on.");
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

        // Engines whose saved run has been looked over since the game loaded.
        private readonly HashSet<string> _checked = new HashSet<string>();

        private void Tick(BaseLocomotive loco, Plan plan)
        {
            if (!plan.Enabled)
            {
                if (!plan.Held)
                    Hold(loco, plan);
                return;
            }
            if (plan.Held)
            {
                Resume(loco, plan);
                return;
            }
            // A whole-train wye turn saved mid-way may have been planned by an older version, so
            // plan it again from where the train is. (A turn that has cut cars off carries on.)
            if (_checked.Add(loco.id) && plan.Phase == Phase.Turning && plan.TurnMoves.All(m => StepKind(m) == "go"))
            {
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} was partway through a wye turn when the game was saved; planning it again.");
                plan.TurnMoves = null;
                plan.Turned = false;
                plan.TurnPending = true;
                Leave(loco, plan, Route(plan));
                return;
            }
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            // Someone took the controls: hand the train over.
            if (ae.Mode != AutoEngineerMode.Waypoint && plan.Phase != Phase.AtStation && plan.Phase != Phase.Fueling && !(plan.Phase == Phase.Layover && plan.ParkedUp))
            {
                End(loco, "Stopped: the Auto Engineer was taken off waypoints.");
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} handed over; the Auto Engineer was taken off waypoints.");
                return;
            }
            // Someone gave it another waypoint: by hand, or another mod such as Waypoint Queue. Hand
            // the train over rather than fight over it.
            if (ae.Mode == AutoEngineerMode.Waypoint && ae.Orders.Waypoint.HasValue && !OurWaypoint(plan, ae.Orders.Waypoint.Value.LocationString))
            {
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName}'s waypoint was changed to {ae.Orders.Waypoint.Value.LocationString} (Conductor's was {plan.Sent}); handing over.");
                End(loco, "Stopped: something else changed the Auto Engineer's waypoint.");
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} handed over; something else changed its waypoint (by hand, or another mod such as Waypoint Queue).");
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
                case Phase.Meeting:
                    Meeting(loco, plan, ae, route);
                    break;
                case Phase.Layover:
                    LaidUp(loco, plan, ae, route);
                    break;
                default:
                    Head(loco, plan, route[0].Stop);
                    break;
            }
        }

        private void ToStation(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            Station station = route.FirstOrDefault(s => s.Id == plan.Station);
            if (station != null && plan.MeetTrains && LookAhead(loco, plan))
                return;
            if (station == null)
            {
                // The station was unpicked on the way: go on to the next one.
                Head(loco, plan, Next(plan, route, plan.Station));
                return;
            }
            // Which end of the platform is the far one is only sure from close by, so aim again then.
            if (!plan.Aimed && ae.Orders.Waypoint.HasValue && Fuel.Flat(Graph.Shared.GetPosition(loco.LocationF), Center(station.Stop)) < ReAimMeters)
            {
                plan.Aimed = true;
                Location? platform = Platform(station.Stop, loco);
                if (platform != null)
                    Send(loco, plan, platform.Value);
                return;
            }
            if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                return;
            if (AtStation(loco, station.Stop))
            {
                Arrive(loco, plan, station.Stop);
                return;
            }
            // Stopped short (aimed at the wrong end, a switch against it, a car in the way): try again, then give up.
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
            plan.Parked = false;
            List<Station> route = Route(plan);
            // Leaving from an end of the line, the train goes back the other way.
            int i = route.FindIndex(s => s.Id == stop.identifier);
            if (i == 0)
                plan.Direction = 1;
            else if (i == route.Count - 1)
                plan.Direction = -1;
            // The last run's done: at an end of the line after the cutoff, everyone off and nobody on.
            plan.LayingUp = plan.LayUp && (i == 0 || i == route.Count - 1) && Layover.IsNight(plan);
            // Board passengers for the stations ahead.
            SetDestinations(loco, plan.LayingUp ? new List<string>() : Ahead(plan, route, stop.identifier));
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
            int getting = coaches.Sum(c => GettingOff(c, stop.identifier));
            HashSet<string> ahead = new HashSet<string>(Ahead(plan, route, stop.identifier));
            int waiting = ahead.Sum(stop.GetTotalWaitingForDestination);
            bool full = coaches.All(Full);
            if (plan.LayingUp)
            {
                plan.Status = getting > 0 ? $"At {stop.DisplayName}, last run done: {getting.Pluralize("passenger")} getting off" : $"At {stop.DisplayName}, last run done";
                int aboard = coaches.Sum(c => (c.GetPassengerMarker() ?? PassengerMarker.Empty()).TotalPassengers);
                if (waited < MinDwell || aboard > 0)
                    return;
                StartLayover(loco, plan, stop);
                return;
            }
            if (getting > 0)
                plan.Status = $"At {stop.DisplayName}: {getting.Pluralize("passenger")} getting off";
            else if (waiting > 0 && !full)
                plan.Status = $"At {stop.DisplayName}: {waiting} boarding";
            else
                plan.Status = $"At {stop.DisplayName}: all aboard";
            if (waited < MinDwell || getting > 0 || (waiting > 0 && !full))
                return;
            // The cutoff came while it stood at an end: lay up rather than start a run, unless
            // someone has already boarded.
            int at = route.FindIndex(s => s.Id == stop.identifier);
            if (plan.LayUp && (at == 0 || at == route.Count - 1) && Layover.IsNight(plan)
                && coaches.Sum(c => (c.GetPassengerMarker() ?? PassengerMarker.Empty()).TotalPassengers) == 0)
            {
                plan.LayingUp = true;
                SetDestinations(loco, new List<string>());
                StartLayover(loco, plan, stop);
                return;
            }
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} leaving {stop.DisplayName}: boarding for {string.Join(", ", ahead)}; {waiting} still waiting for them, coaches {(full ? "full" : "not full")}.");
            Leave(loco, plan, route);
        }

        // Done at a station: refuel first if the engine is low, then on to the next station.
        private void Leave(BaseLocomotive loco, Plan plan, List<Station> route)
        {
            PassengerStop next = Next(plan, route, plan.Station);
            int at = route.FindIndex(s => s.Id == plan.Station);
            bool atEnd = at == 0 || at == route.Count - 1;
            string here = at >= 0 ? route[at].Name : plan.Station;
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check at {here}: turn on wyes {plan.TurnOnWyes}, end of line {atEnd}, turn pending {plan.TurnPending}, already turned {plan.Turned}.");
            if (plan.TurnOnWyes && !plan.Turned && (atEnd || plan.TurnPending))
            {
                plan.Turned = true;
                string note = StartTurn(loco, plan, next, out bool noWyeHere);
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check at {here}: {(note == null ? "turning" : noWyeHere ? "no wye here, turn pending" : note.Length == 0 ? "no turn needed" : note)}.");
                if (note == null)
                {
                    plan.TurnPending = false;
                    return;
                }
                if (noWyeHere)
                {
                    // Run on with the engine at the back and turn at the next station with a wye.
                    if (atEnd && !plan.TurnPending)
                        Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} has no wye at {route[at].Name}; it'll turn at the next station that has one.");
                    plan.TurnPending = true;
                }
                else
                {
                    plan.TurnPending = false;
                    if (note.Length > 0)
                        Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} {note}");
                }
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

        // Turns the train so the engine leads for the next leg: the whole train on this station's
        // wye when it fits the tail, otherwise the engine on its own (see Turns). Returns null when a
        // turn has started, "" when none is needed (or noWyeHere), or why it can't.
        private static string StartTurn(BaseLocomotive loco, Plan plan, PassengerStop next, out bool noWyeHere)
        {
            noWyeHere = false;
            Graph graph = Graph.Shared;
            Location? platform = Platform(next, loco);
            if (platform == null)
            {
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check: no platform found at {next?.DisplayName}, so no turn.");
                return "";
            }
            Vector3 toward = graph.GetPosition(platform.Value);
            List<Car> train = loco.EnumerateCoupled().ToList();
            Vector3 endA = graph.GetPosition(train[0].LocationFor(Car.LogicalEnd.A));
            Vector3 endB = graph.GetPosition(train[train.Count - 1].LocationFor(Car.LogicalEnd.B));
            Vector3 front = graph.GetPosition(loco.LocationF), back = graph.GetPosition(loco.LocationR);
            Vector3 farEnd = Fuel.Flat(endA, front) > Fuel.Flat(endB, front) ? endA : endB;
            bool atFront = Fuel.Flat(front, toward) < Fuel.Flat(farEnd, toward);
            bool facing = Fuel.Flat(front, toward) < Fuel.Flat(back, toward);
            float length = AutoEngineerPlanner.CalculateTotalLength(train);
            // Only this station's own wye, not one out past the next station.
            string stationId = plan.Station;
            Wye wye = Wyes.All().Where(w => w.StopId == stationId && Fuel.Flat(w.Center, front) < 3000f).OrderBy(w => Fuel.Flat(w.Center, front)).FirstOrDefault();
            string wyes = string.Join(", ", Wyes.All().Select(w => $"{w.Name} ({w.StopId}, {Fuel.Flat(w.Center, front):0} m off, tail {w.TailRoom:0} m)"));
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check at {stationId} for {next.DisplayName}: engine front {Fuel.Flat(front, toward):0} m and back {Fuel.Flat(back, toward):0} m from the next platform (facing {facing}), "
                + $"engine end {Fuel.Flat(front, toward):0} m vs far end {Fuel.Flat(farEnd, toward):0} m (engine at front {atFront}), train {length:0} m; "
                + $"this station's wye: {(wye != null ? wye.Name : "none")}; all wyes: {(wyes.Length > 0 ? wyes : "none")}.");
            if (atFront && facing)
                return "";
            bool needTurn = !facing;
            if (needTurn && wye == null)
            {
                noWyeHere = true;
                return "";
            }
            List<string> steps;
            if (needTurn && !atFront && length <= wye.TailRoom)
            {
                // The whole train: in by one leg, all the way into the tail, out by the other leg.
                steps = Turns.WyeSteps(graph, wye, length, toward, "the train");
            }
            else
            {
                steps = Turns.PlanEngine(loco, wye, toward, needTurn, plan.Parked, out string why);
                if (steps == null)
                {
                    ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check: engine-only turn not possible: {why}");
                    return why;
                }
            }
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} wye check: turn planned, {steps.Count} steps: {string.Join(" / ", steps)}");
            plan.TurnMoves = steps.ToArray();
            plan.TurnStep = 0;
            plan.Tries = 0;
            plan.Phase = Phase.Turning;
            IssueStep(loco, plan, steps[0]);
            return null;
        }

        // Through switches, into wye tails and up to coaches at yard speed.
        private static int TurnMph(Plan plan) => Math.Min(10, plan.SpeedMph);

        private void Turning(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                return;
            string[] steps = plan.TurnMoves;
            int current = plan.TurnStep;
            if (current < steps.Length)
            {
                string problem = FinishStep(loco, plan, steps[current]);
                if (problem == "")
                    return;
                if (problem != null)
                {
                    End(loco, "Stopped while turning: " + problem);
                    Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} stopped while turning: {problem}");
                    return;
                }
            }
            int next = current + 1;
            if (next < steps.Length)
            {
                if (StepKind(steps[next]) == "replan")
                {
                    // In a passing siding now: plan the turn again from here.
                    plan.TurnMoves = null;
                    plan.Parked = true;
                    plan.Turned = false;
                    plan.TurnPending = true;
                    Leave(loco, plan, route);
                    return;
                }
                plan.TurnStep = next;
                plan.Tries = 0;
                IssueStep(loco, plan, steps[next]);
                return;
            }
            plan.TurnMoves = null;
            PassengerStop after = Next(plan, route, plan.Station);
            Location? platform = after != null ? Platform(after, loco) : null;
            if (platform != null)
            {
                Vector3 toward = Graph.Shared.GetPosition(platform.Value);
                bool facing = Fuel.Flat(Graph.Shared.GetPosition(loco.LocationF), toward) < Fuel.Flat(Graph.Shared.GetPosition(loco.LocationR), toward);
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} turn done; engine {(facing ? "faces" : "does NOT face")} {after.DisplayName}.");
            }
            Leave(loco, plan, route);
        }

        private static string StepKind(string step) => step.IndexOf(Turns.Sep) < 0 ? (step == "replan" ? "replan" : "go") : step.Substring(0, step.IndexOf(Turns.Sep));

        // Starts a turn step: a move, a coupling, a cut, or brakes off.
        private static void IssueStep(BaseLocomotive loco, Plan plan, string step)
        {
            plan.Since = TimeWeather.Now.TotalSeconds;
            string[] p = step.Split(Turns.Sep);
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} turn step {plan.TurnStep + 1} of {plan.TurnMoves.Length}: {step}");
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            switch (StepKind(step))
            {
                case "go":
                    // A bare location is a turn saved by an older version.
                    Order(loco, plan, new OrderWaypoint(p.Length > 1 ? p[1] : p[0], null), TurnMph(plan));
                    if (p.Length > 2)
                        plan.Status = p[2];
                    break;
                case "couple":
                    Order(loco, plan, new OrderWaypoint(p[1], p[2]), TurnMph(plan));
                    plan.Status = p[3];
                    break;
                case "cut":
                    Turns.CutCar(loco, p[1], p[2] == "loco");
                    plan.Status = p[3];
                    break;
                case "release":
                    Turns.Release(loco);
                    plan.Status = p[1];
                    break;
            }
        }

        // Whether a step is done: null when it is, "" to wait, otherwise what went wrong.
        private static string FinishStep(BaseLocomotive loco, Plan plan, string step)
        {
            string[] p = step.Split(Turns.Sep);
            switch (StepKind(step))
            {
                case "couple":
                    Car car = loco.EnumerateCoupled().FirstOrDefault(c => c.id == p[2]);
                    if (car != null)
                    {
                        Turns.ConnectAir(car);
                        Turns.UpdateCars(loco);
                        return null;
                    }
                    if (++plan.Tries <= 1)
                    {
                        IssueStep(loco, plan, step);
                        return "";
                    }
                    string name = TrainController.Shared.TryGetCarForId(p[2], out Car target) ? target.DisplayName : "the train";
                    return $"couldn't couple to {name}. Its coaches are standing with their handbrakes on.";
                case "cut":
                    // Give the Auto Engineer a moment to see the shorter train.
                    return TimeWeather.Now.TotalSeconds - plan.Since < 3 ? "" : null;
                default:
                    return null;
            }
        }

        // Off to park for the night, or staying put at the station.
        private static void StartLayover(BaseLocomotive loco, Plan plan, PassengerStop stop)
        {
            plan.Phase = Phase.Layover;
            plan.ParkedUp = false;
            plan.Since = TimeWeather.Now.TotalSeconds;
            Location? spot = Layover.FindSpot(loco, stop, out string where);
            plan.ParkSpot = spot.HasValue ? Graph.LocationToString(spot.Value) : "";
            plan.ParkedAt = where;
            if (spot.HasValue)
                Send(loco, plan, spot.Value, TurnMph(plan));
            plan.Status = $"Going to park for the night {where}";
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} laying up for the night {where}.");
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is done for the night and will lay up {where} until {Layover.Clock(plan.ResumeMinutes)}.");
        }

        // Parked for the night; in the morning, back to the station to pick up passengers.
        private static void LaidUp(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            if (!plan.ParkedUp)
            {
                if (!loco.IsStopped(2f) || ae.Orders.Waypoint.HasValue)
                    return;
                plan.ParkedUp = true;
            }
            if (Layover.IsNight(plan))
            {
                plan.Status = $"Laid up {plan.ParkedAt} until {Layover.Clock(plan.ResumeMinutes)}";
                return;
            }
            plan.LayingUp = false;
            plan.ParkedUp = false;
            plan.ParkSpot = "";
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} is starting the day's service.");
            Station station = route.FirstOrDefault(s => s.Id == plan.Station);
            Head(loco, plan, station != null ? station.Stop : route[plan.Direction > 0 ? 0 : route.Count - 1].Stop);
        }

        // Every few seconds on the way to a station: a train coming the other way? Then into a passing
        // siding to wait for it. True when a meet has started.
        private bool LookAhead(BaseLocomotive loco, Plan plan)
        {
            float now = Time.realtimeSinceStartup;
            if (_lookedAhead.TryGetValue(loco.id, out float last) && now - last < LookEvery)
                return false;
            _lookedAhead[loco.id] = now;
            Meet meet = Meets.Find(loco, plan.SpeedMph, out BaseLocomotive other, out string why);
            if (meet == null)
            {
                if (other != null && why != null && plan.MeetWarned != other.id)
                {
                    plan.MeetWarned = other.id;
                    Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} sees {Hyperlink.To(other)} coming the other way, but {why}; carrying on.");
                }
                return false;
            }
            plan.Phase = Phase.Meeting;
            plan.MeetWith = other.id;
            plan.MeetSiding = string.Join(";", meet.Siding.Select(s => s.id));
            plan.MeetSpot = meet.Spot.HasValue ? Graph.LocationToString(meet.Spot.Value) : "";
            plan.MeetWaiting = false;
            plan.Since = TimeWeather.Now.TotalSeconds;
            if (meet.Spot.HasValue)
            {
                Send(loco, plan, meet.Spot.Value);
                plan.Status = $"Taking the {(meet.OnSiding ? "siding" : "main")} at {meet.Where} to meet {other.DisplayName}";
            }
            else
            {
                var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
                if (ae.Orders.Waypoint.HasValue)
                    ae.ClearWaypoint();
                plan.Status = $"Holding at {meet.Where} to meet {other.DisplayName}";
            }
            Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} will meet {Hyperlink.To(other)} at {meet.Where}, on the {(meet.OnSiding ? "siding" : "main")}.");
            return true;
        }

        // Into the siding, then waiting until the other train has gone by.
        private void Meeting(BaseLocomotive loco, Plan plan, AutoEngineerOrdersHelper ae, List<Station> route)
        {
            TrainController.Shared.TryGetCarForId(plan.MeetWith, out Car found);
            var other = found as BaseLocomotive;
            var siding = new HashSet<TrackSegment>(plan.MeetSiding.Split(';').Where(id => id.Length > 0)
                .Select(id => Graph.Shared.GetSegment(id)).Where(s => s != null));
            bool stopped = loco.IsStopped(2f) && !ae.Orders.Waypoint.HasValue;
            if (stopped && !plan.MeetWaiting)
            {
                plan.MeetWaiting = true;
                plan.Since = TimeWeather.Now.TotalSeconds;
            }
            if (plan.MeetWaiting)
                plan.Status = $"Waiting for {(other != null ? other.DisplayName : "a train")} to pass";
            bool passed = Meets.Passed(other, siding);
            bool tired = plan.MeetWaiting && TimeWeather.Now.TotalSeconds - plan.Since > MeetPatience;
            if (!passed && !tired)
                return;
            if (tired && !passed)
                Multiplayer.Broadcast($"Conductor: {Hyperlink.To(loco)} waited half an hour for {(other != null ? Hyperlink.To(other) : "a train")}, which hasn't come; carrying on.");
            plan.MeetWith = "";
            plan.MeetSiding = "";
            plan.MeetSpot = "";
            plan.MeetWaiting = false;
            plan.Phase = Phase.ToStation;
            _lookedAhead[loco.id] = Time.realtimeSinceStartup;
            Station station = route.FirstOrDefault(s => s.Id == plan.Station);
            Head(loco, plan, station != null ? station.Stop : Next(plan, route, plan.Station));
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
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} heading for {Fuel.Describe(loader.load.id).ToLowerInvariant()} near {Wyes.NearestStop(Fuel.Point(loader))?.DisplayName}: {Fuel.Describe(loader.load.id)} at {Fuel.Level(loco, loader.load.id):P0}, refuel below {plan.RefuelBelow}%.");
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
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} taking {what}, starting at {Fuel.Level(loco, loader.load.id):P0}.");
                return;
            }
            // A pull-up that missed teaches the next one. Backing clear of the spout counts as a
            // move too, so the limit allows three pull-ups each with a back-up between.
            if (Fuel.PulledUp(loco))
                Fuel.NoteMiss(loco, loader);
            if (++plan.Tries > MaxFuelMoves)
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
            double took = now - plan.Since;
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} done taking {what} at {level:P0} after {took / 60:0.0} game minutes{(stalled && level < 0.995f ? ", as it stopped filling" : "")}.");
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

        // The next station from wherever the train is now, as it may have been driven by hand while
        // Conductor was off: the station ahead of it on the stretch of line it's on, the way it's
        // moving (or the way the run was going, when it's stopped).
        private static PassengerStop NextFromHere(BaseLocomotive loco, Plan plan, List<Station> route)
        {
            // Standing at one of the stations: make the stop there.
            Station here = route.FirstOrDefault(st => AtStation(loco, st.Stop));
            if (here != null)
            {
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} back on at {here.Name}: stopping there (was heading for {plan.Station}).");
                return here.Stop;
            }
            Graph graph = Graph.Shared;
            Vector3 front = graph.GetPosition(loco.LocationF), back = graph.GetPosition(loco.LocationR);
            int best = -1;
            float bestOff = float.MaxValue;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                Vector3 a = Center(route[i].Stop), b = Center(route[i + 1].Stop);
                float off = Fuel.Flat(front, a) + Fuel.Flat(front, b) - Fuel.Flat(a, b);
                if (off < bestOff)
                {
                    bestOff = off;
                    best = i;
                }
            }
            if (best < 0)
                return StationStop(plan) ?? Next(plan, route, plan.Station);
            Vector3 from = Center(route[best].Stop), to = Center(route[best + 1].Stop);
            if (Mathf.Abs(loco.velocity) > 0.5f)
            {
                Vector3 heading = (front - back) * Mathf.Sign(loco.velocity);
                plan.Direction = Vector3.Dot(heading, to - from) >= 0f ? 1 : -1;
            }
            PassengerStop next = route[plan.Direction > 0 ? best + 1 : best].Stop;
            ModEntry.Log?.Info($"Conductor: {loco.DisplayName} back on between {route[best].Name} and {route[best + 1].Name}, "
                + $"going {(plan.Direction > 0 ? "toward " + route[route.Count - 1].Name : "toward " + route[0].Name)}: heading for {next.DisplayName} (was {plan.Station}).");
            return next;
        }

        // Puts the Auto Engineer on waypoints, as Conductor drives by them, if it was in manual,
        // road or yard. A train standing at a station stays put until it's sent somewhere.
        private static void WaypointMode(BaseLocomotive loco, Plan plan)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            if (ae.Mode != AutoEngineerMode.Waypoint)
                ae.SetOrdersValue(AutoEngineerMode.Waypoint, null, plan.SpeedMph);
        }

        // Gives the Auto Engineer a waypoint, noting it as Conductor's own.
        private static void Order(BaseLocomotive loco, Plan plan, OrderWaypoint waypoint, int mph)
        {
            plan.Sent = waypoint.LocationString;
            new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject)).SetWaypointOrder(waypoint, mph);
        }

        // Whether the Auto Engineer's waypoint is still the one Conductor gave it (within a couple of
        // meters, should the game tidy the location up).
        private static bool OurWaypoint(Plan plan, string current)
        {
            string sent = plan.Sent;
            if (string.IsNullOrEmpty(sent) || current == sent)
                return true;
            try
            {
                Graph graph = Graph.Shared;
                return Vector3.Distance(graph.GetPosition(graph.ResolveLocationString(current)), graph.GetPosition(graph.ResolveLocationString(sent))) < 2f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Passengers in a coach who get off at a station: those going there, and, as the game has it,
        // any going somewhere the coach is no longer set to go.
        private static int GettingOff(Car coach, string stopId)
        {
            PassengerMarker marker = coach.GetPassengerMarker() ?? PassengerMarker.Empty();
            HashSet<string> going = marker.Destinations ?? new HashSet<string>();
            return marker.Groups.Where(g => g.Destination == stopId || !going.Contains(g.Destination)).Sum(g => g.Count);
        }

        // Which way a train is going along the list from a station partway along it: the way it's
        // moving, or when it's stopped, the way the engine faces.
        private static int WayGoing(BaseLocomotive loco, List<Station> route, int at)
        {
            Graph graph = Graph.Shared;
            Vector3 heading = graph.GetPosition(loco.LocationF) - graph.GetPosition(loco.LocationR);
            if (Mathf.Abs(loco.velocity) > 0.5f)
                heading *= Mathf.Sign(loco.velocity);
            Vector3 down = Center(route[at + 1].Stop) - Center(route[at - 1].Stop);
            return Vector3.Dot(heading, down) >= 0f ? 1 : -1;
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
            plan.Aimed = false;
            Send(loco, plan, platform.Value);
            plan.Status = $"Heading for {stop.DisplayName}{(Platforms.LastWasSiding ? " (siding)" : "")}";
        }

        private static void Send(BaseLocomotive loco, Plan plan, Location location, int? mph = null)
        {
            var ae = new AutoEngineerOrdersHelper(loco, new AutoEngineerPersistence(loco.KeyValueObject));
            Order(loco, plan, new OrderWaypoint(Graph.LocationToString(location), null), mph ?? plan.SpeedMph);
        }

        // Where to stop at a station: near the far end of its longest platform track, so the
        // whole train pulls in along it.
        // Where to stop at a station: see Platforms.
        public static Location? Platform(PassengerStop stop, BaseLocomotive loco) =>
            Platforms.StopAt(stop, loco, new Plan(loco).PreferSidings);

        private static Vector3 Center(PassengerStop stop)
        {
            TrackSpan span = stop.TrackSpans.FirstOrDefault(t => t != null);
            return span != null ? span.GetCenterPoint() : Vector3.zero;
        }

        // Whether the train is in at the station: a coach on the platform track, as the station
        // only boards coaches there (or any car, for a train with no coaches).
        private static bool AtStation(BaseLocomotive loco, PassengerStop stop)
        {
            List<Car> cars = loco.EnumerateCoupled().ToList();
            bool anyCoach = cars.Any(c => c.IsPassengerCar());
            var train = new HashSet<Car>(cars.Where(c => !anyCoach || c.IsPassengerCar()));
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
