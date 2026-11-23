using System.Collections.Generic;
using System.Linq;
using Core;
using Model;
using Model.AI;
using Model.Ops;
using RollingStock;
using Track;
using UI;
using UI.Builder;
using UI.CompanyWindow;
using UI.Windows;
using UnityEngine;

namespace Conductor
{
    // One engine's Conductor settings: what it's doing, the stations it calls at, and its fuel stops.
    internal sealed class ConductorWindow : BuilderWindowBase
    {
        private static ConductorWindow _shared;
        private static BaseLocomotive _loco;
        // A one-off note under the fuel stops, such as why a pick didn't take.
        private static string _fuelNote;

        public override string Identifier => "jamesplotts.conductor";

        protected override string Title => _loco != null ? "Conductor: " + _loco.DisplayName : "Conductor";

        public override WindowConfig Config => WindowConfig.Resizable(new Vector2Int(588, 760), new Vector2Int(340, 300), Window.Position.UpperRight);

        public static void Show(BaseLocomotive loco)
        {
            // The game destroys mod windows when a map unloads; make a new one when that's happened.
            if (_shared == null)
            {
                WindowManager manager = WindowManager.Shared;
                if (manager == null)
                    return;
                _shared = manager.CreateWindowWithComponent<ConductorWindow>();
            }
            _loco = loco;
            _fuelNote = null;
            _shared.Show();
        }

        public static void CloseIfOpen()
        {
            if (_shared != null && _shared.Window.IsShown)
                _shared.Window.CloseWindow();
        }

        protected override void BuildContent(UIPanelBuilder builder)
        {
            BaseLocomotive loco = _loco;
            if (loco == null)
            {
                builder.AddLabelEmptyState("Open Conductor from an engine's Car tab.");
                return;
            }
            var plan = new Plan(loco);
            bool canWrite = plan.AuthorizedToWrite;
            builder.VScrollView(scroll =>
            {
                scroll.AddFieldToggle("Enabled", () => new Plan(loco).Enabled, on =>
                {
                    var p = new Plan(loco);
                    p.Enabled = on;
                    builder.Rebuild();
                }, canWrite);
                if (!plan.Enabled && !plan.Running)
                {
                    scroll.AddLabel("<size=85%>Conductor is off for this engine. Turn it on for a passenger engine; leave it off for freight and work trains, and Conductor won't touch them.</size>");
                    return;
                }
                scroll.AddField("Status", () => Status(loco), UIPanelBuilder.Frequency.Periodic);
                scroll.AddField("Aboard", () => Aboard(loco), UIPanelBuilder.Frequency.Periodic);
                scroll.AddField("Fuel", () => Levels(loco), UIPanelBuilder.Frequency.Periodic);
                scroll.ButtonStrip(strip =>
                {
                    if (!new Plan(loco).Running)
                    {
                        strip.AddButton("Start", () =>
                        {
                            string problem = Runner.Begin(loco);
                            if (problem != null)
                                new Plan(loco).Status = problem;
                            builder.Rebuild();
                        });
                    }
                    else
                    {
                        strip.AddButton("Stop", () =>
                        {
                            Runner.End(loco, "Stopped.");
                            builder.Rebuild();
                        });
                        if (!new Plan(loco).Timetable)
                            strip.AddButton("Turn Back", () => Runner.Reverse(loco));
                    }
                });
                if (!plan.Running && !plan.Timetable)
                {
                    List<Station> line = Runner.Route(plan);
                    if (line.Count >= 2)
                    {
                        string first = line[0].Name, last = line[line.Count - 1].Name;
                        scroll.AddField("First Toward", scroll.AddDropdownIntPicker(new List<int> { 0, -1, 1 }, plan.FirstWay,
                            w => w > 0 ? last : w < 0 ? first : "The way it's going", canWrite, w =>
                            {
                                var p = new Plan(loco);
                                p.FirstWay = w;
                            }));
                    }
                }
                scroll.AddField("Speed", scroll.AddDropdownIntPicker(new List<int> { 15, 20, 25, 30, 35, 40, 45, 50 }, plan.SpeedMph, mph => $"{mph} mph", canWrite, mph =>
                {
                    var p = new Plan(loco);
                    p.SpeedMph = mph;
                }));

                scroll.AddSection(Heading("Timetable"), section => TimetableSection(section, builder, loco, canWrite, plan.Running));
                scroll.AddSection(Heading("Stations"), section => Stations(section, loco, canWrite));
                scroll.AddSection(Heading("Fuel Stops"), section => FuelStops(section, loco, canWrite));
                scroll.AddSection(Heading("Overnight"), section => Overnight(section, loco, canWrite));
                scroll.AddSection(Heading("Turn Around"), section => Turning(section, loco, canWrite));
            });
            if (plan.Enabled || plan.Running)
            {
                // Conductor's messages for this engine, newest at the top.
                builder.Spacer(4f);
                builder.AddLabel("<b>Log</b>");
                builder.VScrollView(log => log.AddLabel(() => "<size=85%>" + Journal.For(loco.DisplayName) + "</size>", UIPanelBuilder.Frequency.Periodic))
                    .Height(170f);
            }
            // Running state comes from the runner; show the buttons that fit.
            builder.RebuildOnPropertyChange(new Helpers.KeyValueProperties(loco.KeyValueObject, "conductor"), "running");
            builder.RebuildOnPropertyChange(new Helpers.KeyValueProperties(loco.KeyValueObject, "conductor"), "enabled");
            builder.RebuildOnPropertyChange(new Helpers.KeyValueProperties(loco.KeyValueObject, "conductor"), "tt");
        }

        private static void Stations(UIPanelBuilder section, BaseLocomotive loco, bool canWrite)
        {
            List<Station> stations = Line.Stations();
            if (stations.Count == 0)
            {
                section.AddLabel("No stations found.");
                return;
            }
            section.AddLabel("<size=85%>The train runs the ticked stations top to bottom, then back. The first and last ticked are the ends of the line.</size>");
            section.AddFieldToggle("Use Station Sidings", () => new Plan(loco).PreferSidings, on =>
            {
                var p = new Plan(loco);
                p.PreferSidings = on;
            }, canWrite);
            section.AddFieldToggle("Avoid Oncoming Trains", () => new Plan(loco).MeetTrains, on =>
            {
                var p = new Plan(loco);
                p.MeetTrains = on;
            }, canWrite);
            bool timetable = new Plan(loco).Timetable;
            if (timetable)
                section.AddLabel("<size=85%>Timetable Mode is on: the stations come from the timetable trains, so the ticks below aren't used.</size>");
            HashSet<string> picked = new Plan(loco).Stops;
            List<string> open = stations.Where(s => s.Closed == null && picked.Contains(s.Id)).Select(s => s.Id).ToList();
            foreach (Station station in stations)
            {
                Station s = station;
                string name = (s.Depth > 0 ? "    ↳ " : "") + s.Name;
                if (open.Count >= 2 && (s.Id == open.First() || s.Id == open.Last()))
                    name += "  <color=#E0A040>end</color>";
                // Stations not bought or built to yet aren't shown; they appear once they open.
                if (s.Closed != null || timetable)
                    continue;
                section.AddFieldToggle(name, () => new Plan(loco).Stops.Contains(s.Id), on =>
                {
                    var p = new Plan(loco);
                    HashSet<string> stops = p.Stops;
                    if (on)
                        stops.Add(s.Id);
                    else
                        stops.Remove(s.Id);
                    p.Stops = stops;
                    section.Rebuild();
                }, canWrite);
            }

            var plan = new Plan(loco);
            section.AddField("Shortest Stop", section.AddDropdownIntPicker(new List<int> { 30, 60, 120, 180, 300, 600 }, plan.MinStopSeconds,
                sec => sec < 60 ? $"{sec} seconds" : sec == 60 ? "1 minute" : $"{sec / 60} minutes", canWrite, sec =>
                {
                    var p = new Plan(loco);
                    p.MinStopSeconds = sec;
                }));
            section.AddLabel("<size=85%>The train stays at least this long at every stop, even when nobody is getting on or off. Give it longer if another mod loads baggage or mail at stations.</size>");

            // Wait to fill up, per station.
            List<Station> route = Runner.Route(plan);
            if (plan.Timetable)
            {
                HashSet<string> ticked = plan.TimetableTrains;
                var ids = new HashSet<string>(Timetables.PassengerTrains().Where(t => ticked.Contains(t.Name)).SelectMany(Timetables.Stations).Select(st => st.Id));
                route = stations.Where(st => st.Closed == null && ids.Contains(st.Id)).ToList();
            }
            if (route.Count < 2)
                return;
            section.AddLabel("<size=85%><b>Wait to Fill Up</b>: at these stations the train doesn't leave until the coaches are this full, or until it has waited as long as Wait No Longer Than.</size>");
            Dictionary<string, int> holds = plan.HoldPercent;
            foreach (Station station in route)
            {
                Station s = station;
                section.AddField("    " + s.Name, section.AddDropdownIntPicker(new List<int> { 0, 25, 50, 75, 90, 100 }, holds.TryGetValue(s.Id, out int pct) ? pct : 0,
                    v => v == 0 ? "Don't wait" : $"{v}% full", canWrite, v =>
                    {
                        var p = new Plan(loco);
                        Dictionary<string, int> h = p.HoldPercent;
                        h[s.Id] = v;
                        p.HoldPercent = h;
                    }));
            }
            section.AddField("Wait No Longer Than", section.AddDropdownIntPicker(new List<int> { 15, 30, 60, 90, 120, 180 }, plan.HoldMaxMinutes,
                m => m < 60 ? $"{m} minutes" : m == 60 ? "1 hour" : m % 60 == 0 ? $"{m / 60} hours" : $"{m / 60.0:0.#} hours", canWrite, m =>
                {
                    var p = new Plan(loco);
                    p.HoldMaxMinutes = m;
                }));
        }

        private static void TimetableSection(UIPanelBuilder section, UIPanelBuilder window, BaseLocomotive loco, bool canWrite, bool running)
        {
            section.AddFieldToggle("Timetable Mode", () => new Plan(loco).Timetable, on =>
            {
                var p = new Plan(loco);
                if (p.Running)
                    Runner.End(loco, "Stopped: Timetable Mode changed. Press Start to carry on.");
                p.Timetable = on;
                window.Rebuild(); // Stations, Turn Around and the buttons change too
            }, canWrite);
            var plan = new Plan(loco);
            if (!plan.Timetable)
            {
                section.AddLabel("<size=85%>Off: the train runs back and forth between the ticked stations. On: it runs the game's timetable trains you tick here, keeping to their times.</size>");
                return;
            }
            List<Model.Ops.Timetable.Timetable.Train> trains = Timetables.PassengerTrains();
            if (trains.Count == 0)
            {
                section.AddLabel("<color=#E0A040>The game's timetable has no passenger trains. Turn the timetable on (Preferences) and add some in the Timetable window.</color>");
                return;
            }
            section.AddLabel("<size=85%>Tick the trains this engine runs. It calls at each train's stations in order and doesn't leave a station before the train's time there. At the end of one train it goes out as the next ticked train that leaves from there, turning on the wye first if it needs to. With Lay Up Overnight on, a wait of more than 90 minutes is spent parked, back at the platform 30 minutes before the train is due.</size>");
            if (running && plan.TimetableTrain.Length > 0)
                section.AddField("Running As", () => { var t = Timetables.Find(new Plan(loco).TimetableTrain); return t != null ? Timetables.Describe(t) : new Plan(loco).TimetableTrain; }, UIPanelBuilder.Frequency.Periodic);
            foreach (Model.Ops.Timetable.Timetable.Train train in trains)
            {
                string symbol = train.Name;
                section.AddFieldToggle(Timetables.Describe(train), () => new Plan(loco).TimetableTrains.Contains(symbol), on =>
                {
                    var p = new Plan(loco);
                    HashSet<string> set = p.TimetableTrains;
                    if (on)
                        set.Add(symbol);
                    else
                        set.Remove(symbol);
                    p.TimetableTrains = set;
                }, canWrite);
            }
        }

        private static void FuelStops(UIPanelBuilder section, BaseLocomotive loco, bool canWrite)
        {
            List<string> carried = Fuel.LoadsFor(loco);
            if (carried.Count == 0)
            {
                section.AddLabel("This engine doesn't take on fuel.");
                return;
            }
            var plan = new Plan(loco);
            section.AddField("Refuel Below", section.AddDropdownIntPicker(new List<int> { 10, 15, 20, 25, 30, 35, 40, 50, 60, 75 }, plan.RefuelBelow, pct => $"{pct}%", canWrite, pct =>
            {
                var p = new Plan(loco);
                p.RefuelBelow = pct;
            }));
            section.AddFieldToggle("Use Any Open Stop", () => new Plan(loco).AnyFuelStop, on =>
            {
                var p = new Plan(loco);
                p.AnyFuelStop = on;
                section.Rebuild();
            }, canWrite);
            if (plan.AnyFuelStop)
            {
                List<CarLoadTargetLoader> open = Fuel.Open(carried);
                section.AddLabel(open.Count == 0
                    ? "<size=85%>No fuel stops for this engine are open yet.</size>"
                    : "<size=85%>After a station, if it's below the refuel level, the train pulls up at the nearest open fuel stop, fills, and carries on. New ones are used as their areas open.</size>");
                return;
            }
            List<FuelStop> stops = plan.FuelStops.Where(f => carried.Contains(f.LoadId)).ToList();
            if (stops.Count == 0)
                section.AddLabel($"<size=85%>Add the {string.Join(" and ", carried.Select(c => Fuel.Describe(c).ToLowerInvariant()))} stops this train may use. After a station, if it's below the refuel level, it pulls up at the nearest one, fills, and carries on.</size>");
            foreach (FuelStop stop in stops)
            {
                FuelStop f = stop;
                CarLoadTargetLoader loader = Fuel.Find(f.LoaderId);
                string label = $"{Fuel.Describe(f.LoadId)} at {(loader != null ? Near(loader) : "a missing stop")}";
                section.AddField(label, section.AddButton("Remove", () =>
                {
                    var p = new Plan(loco);
                    p.FuelStops = p.FuelStops.Where(x => x.LoaderId != f.LoaderId).ToList();
                    section.Rebuild();
                }).RectTransform);
            }
            if (canWrite)
                section.AddButton("Add Fuel Stop", () => PickFuel(loco, section));
            if (!string.IsNullOrEmpty(_fuelNote))
                section.AddLabel($"<color=#E0A040>{_fuelNote}</color>");
        }

        private static void Overnight(UIPanelBuilder section, BaseLocomotive loco, bool canWrite)
        {
            section.AddFieldToggle("Lay Up Overnight", () => new Plan(loco).LayUp, on =>
            {
                var p = new Plan(loco);
                p.LayUp = on;
                section.Rebuild();
            }, canWrite);
            var plan = new Plan(loco);
            if (!plan.LayUp)
                return;
            List<int> evening = Enumerable.Range(16 * 2, 16).Select(h => h * 30).ToList();
            List<int> morning = Enumerable.Range(3 * 2, 14).Select(h => h * 30).ToList();
            section.AddField("Last Run Starts", section.AddDropdownIntPicker(evening, plan.LastRunMinutes, Layover.Clock, canWrite, m =>
            {
                var p = new Plan(loco);
                p.LastRunMinutes = m;
            }));
            section.AddField("Resume At", section.AddDropdownIntPicker(morning, plan.ResumeMinutes, Layover.Clock, canWrite, m =>
            {
                var p = new Plan(loco);
                p.ResumeMinutes = m;
            }));
            section.AddLabel("<size=85%>Reaching an end of the line at or after the last run's start time, the train lets everyone off, takes nobody on, and parks: on an engine repair track nearby that takes this engine and holds the whole train (the shop works on it overnight), else in a siding nearby, else at the station. At the resume time it comes back to the station and starts the day's service.</size>");
        }

        private static void Turning(UIPanelBuilder section, BaseLocomotive loco, bool canWrite)
        {
            section.AddFieldToggle("Turn on Wyes", () => new Plan(loco).TurnOnWyes, on =>
            {
                var p = new Plan(loco);
                p.TurnOnWyes = on;
            }, canWrite);
            section.AddField("Turn Speed", section.AddDropdownIntPicker(new List<int> { 10, 15, 20 }, new Plan(loco).TurnSpeedMph, mph => $"{mph} mph", canWrite, mph =>
            {
                var p = new Plan(loco);
                p.TurnSpeedMph = mph;
            }));
            section.AddLabel("<size=85%>At each end of the line, if the engine would be at the back for the trip home, the train turns on the station's wye: into one leg, all the way into the tail, and out the other leg. A train too long for the tail stands in a passing siding while the engine turns alone and runs around it. An observation car on the rear goes through the wye with the engine and is set back on the rear, platform outward.</size>");
            if (Wyes.All().Count == 0)
                section.AddLabel("<size=85%>No wyes found on open track yet.</size>");
        }

        // A section heading, bolder and larger than the game's own.
        private static string Heading(string title) => $"<b><size=120%>{title}</size></b>";

        private static void PickFuel(BaseLocomotive loco, UIPanelBuilder section)
        {
            TrackLocationPicker picker = TrackLocationPicker.Shared;
            if (picker == null)
                return;
            picker.StartPicking(new TrackLocationPickRequest
            {
                Prompt = "Click the track beside a water spout, coal chute or diesel stand.",
                CanSelect = _ => true,
                OnLocationSelected = location =>
                {
                    CarLoadTargetLoader loader = Fuel.Nearest(location, Fuel.LoadsFor(loco));
                    var p = new Plan(loco);
                    List<FuelStop> stops = p.FuelStops;
                    if (loader == null)
                        _fuelNote = "No fuel stop this engine can use is there. Click the track right beside the spout.";
                    else if (stops.Any(s => s.LoaderId == loader.keyValueObject.RegisteredId))
                        _fuelNote = "That fuel stop is already on the list.";
                    else
                    {
                        stops.Add(new FuelStop { LoaderId = loader.keyValueObject.RegisteredId, LoadId = loader.load.id });
                        p.FuelStops = stops;
                        _fuelNote = null;
                    }
                    section.Rebuild();
                },
                OnCancelled = () => { },
            });
        }

        // The station nearest a fuel stop, to name it by.
        private static string Near(CarLoadTargetLoader loader)
        {
            Vector3 at = Fuel.Point(loader);
            PassengerStop nearest = PassengerStop.FindAll()
                .Where(s => s.TrackSpans.Any(t => t != null))
                .OrderBy(s => Fuel.Flat(s.TrackSpans.First(t => t != null).GetCenterPoint(), at))
                .FirstOrDefault();
            return nearest != null ? nearest.DisplayName : "an unnamed spot";
        }

        private static string Status(BaseLocomotive loco)
        {
            var plan = new Plan(loco);
            if (plan.Running && !plan.Enabled)
                return "<color=#E0A040>Disabled: holding, yours to drive.</color> Was: " + plan.Status;
            if (plan.Running)
                return plan.Status;
            return string.IsNullOrEmpty(plan.Status) ? "Not running" : plan.Status;
        }

        private static string Aboard(BaseLocomotive loco)
        {
            List<Car> coaches = loco.EnumerateCoupled().Where(c => c.IsPassengerCar()).ToList();
            if (coaches.Count == 0)
                return "No coaches coupled";
            List<PassengerMarker> markers = coaches.Select(c => c.GetPassengerMarker() ?? PassengerMarker.Empty()).ToList();
            int aboard = markers.Sum(m => m.TotalPassengers);
            string text = $"{aboard.Pluralize("passenger")} in {(coaches.Count == 1 ? "1 coach" : coaches.Count + " coaches")}";
            // Who's going where, in line order.
            var by = Line.Stations().Select(st => (st.Name, n: markers.Sum(m => m.CountPassengersForStop(st.Id)))).Where(x => x.n > 0).ToList();
            if (by.Count > 0)
                text += ": " + string.Join(", ", by.Select(x => $"{x.n} for {x.Name}"));
            return text;
        }

        private static string Levels(BaseLocomotive loco)
        {
            var plan = new Plan(loco);
            var parts = new List<string>();
            foreach (string load in Fuel.LoadsFor(loco))
            {
                float level = Fuel.Level(loco, load);
                if (level < 0f)
                    continue;
                string text = $"{Fuel.Describe(load)} {level:P0}";
                parts.Add(level * 100f < plan.RefuelBelow ? $"<color=#E0A040>{text}</color>" : text);
            }
            return parts.Count > 0 ? string.Join("  ·  ", parts) : "—";
        }
    }
}
