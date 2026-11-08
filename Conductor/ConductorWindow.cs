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

        public override WindowConfig Config => WindowConfig.Resizable(new Vector2Int(420, 600), new Vector2Int(340, 300), Window.Position.UpperRight);

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
                        strip.AddButton("Turn Back", () => Runner.Reverse(loco));
                    }
                });
                scroll.AddField("Speed", scroll.AddDropdownIntPicker(new List<int> { 15, 20, 25, 30, 35, 40, 45, 50 }, plan.SpeedMph, mph => $"{mph} mph", canWrite, mph =>
                {
                    var p = new Plan(loco);
                    p.SpeedMph = mph;
                }));

                scroll.AddSection("Stations", section => Stations(section, loco, canWrite));
                scroll.AddSection("Fuel Stops", section => FuelStops(section, loco, canWrite));
                scroll.AddSection("Turning", section => Turning(section, loco, canWrite));
            });
            // Running state comes from the runner; show the buttons that fit.
            builder.RebuildOnPropertyChange(new Helpers.KeyValueProperties(loco.KeyValueObject, "conductor"), "running");
            builder.RebuildOnPropertyChange(new Helpers.KeyValueProperties(loco.KeyValueObject, "conductor"), "enabled");
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
            section.AddFieldToggle("Meet Oncoming Trains", () => new Plan(loco).MeetTrains, on =>
            {
                var p = new Plan(loco);
                p.MeetTrains = on;
            }, canWrite);
            HashSet<string> picked = new Plan(loco).Stops;
            List<string> open = stations.Where(s => s.Closed == null && picked.Contains(s.Id)).Select(s => s.Id).ToList();
            foreach (Station station in stations)
            {
                Station s = station;
                string name = (s.Depth > 0 ? "    ↳ " : "") + s.Name;
                if (open.Count >= 2 && (s.Id == open.First() || s.Id == open.Last()))
                    name += "  <color=#E0A040>end</color>";
                // Stations not bought or built to yet aren't shown; they appear once they open.
                if (s.Closed != null)
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
                    : "<size=85%>After a station, if it's below the refuel level, the train pulls up at the nearest of these, fills, and carries on. New ones are used as their areas open.</size>");
                foreach (IGrouping<string, CarLoadTargetLoader> place in open.GroupBy(Near).OrderBy(g => g.Key))
                    section.AddField(place.Key, string.Join(", ", place.Select(l => Fuel.Describe(l.load.id)).Distinct()));
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

        private static void Turning(UIPanelBuilder section, BaseLocomotive loco, bool canWrite)
        {
            section.AddFieldToggle("Turn on Wyes", () => new Plan(loco).TurnOnWyes, on =>
            {
                var p = new Plan(loco);
                p.TurnOnWyes = on;
            }, canWrite);
            section.AddLabel("<size=85%>At each end of the line, if the engine would be at the back for the trip home, the train turns on the station's wye: into one leg, all the way into the tail, and out the other leg. A train too long for the tail stands in a passing siding while the engine turns alone and runs around it. An observation car on the rear goes through the wye with the engine and is set back on the rear, platform outward.</size>");
            List<Wye> wyes = Wyes.All();
            if (wyes.Count == 0)
            {
                section.AddLabel("No wyes found on open track.");
                return;
            }
            float length = AutoEngineerPlanner.CalculateTotalLength(loco.EnumerateCoupled().ToList());
            foreach (Wye wye in wyes.OrderBy(w => w.Name))
            {
                string fits = length <= wye.TailRoom ? "the whole train turns" : "the engine turns alone";
                section.AddField(wye.Name, $"tail {wye.TailRoom * 3.28084f:0} ft, {fits}");
            }
            section.AddLabel($"<size=85%>This train is {length * 3.28084f:0} ft long.</size>");
        }

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
            int aboard = coaches.Sum(c => (c.GetPassengerMarker() ?? PassengerMarker.Empty()).TotalPassengers);
            return $"{aboard.Pluralize("passenger")} in {(coaches.Count == 1 ? "1 coach" : coaches.Count + " coaches")}";
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
