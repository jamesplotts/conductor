using System.Collections.Generic;
using System.Linq;
using Game;
using Model;
using Model.AI;
using Model.Ops;
using Track;
using UnityEngine;

namespace Conductor
{
    // Laying a train up overnight: when it's night by the train's settings, and where to park.
    internal static class Layover
    {
        // How far from the end station to look for somewhere to park, in meters; farther for a repair
        // track when cars need work.
        private const float Search = 3000f;
        private const float RepairSearch = 8000f;
        // A car needs the shop below this condition.
        public const float WornBelow = 0.9f;

        public static bool NeedsWork(Car car) => car.Condition < WornBelow || car.NeedsOiling || car.HasHotbox;

        // Whether a repair track works on this kind of car (an empty filter takes any).
        private static bool Serves(RepairTrack repair, Car car) => repair.carTypeFilter.IsEmpty || repair.carTypeFilter.Matches(car.CarType);

        // Night: after the last run's start time and before the resume time.
        public static bool IsNight(Plan plan)
        {
            int now = Mathf.FloorToInt(TimeWeather.Now.Hours * 60f) % (24 * 60);
            int last = plan.LastRunMinutes, resume = plan.ResumeMinutes;
            return last > resume ? now >= last || now < resume : now >= last && now < resume;
        }

        public static string Clock(int minutes)
        {
            int h = minutes / 60 % 24, m = minutes % 60;
            return $"{(h % 12 == 0 ? 12 : h % 12)}:{m:00} {(h < 12 ? "am" : "pm")}";
        }

        // Where to park for the night: on an engine repair track nearby that takes this engine and holds
        // the whole train, so the shop works on it overnight; else on a passing siding nearby (the siding
        // rather than the main); else null, to stay at the station. where says which, for the status line.
        public static Location? FindSpot(BaseLocomotive loco, PassengerStop stop, out string where)
        {
            Graph graph = Graph.Shared;
            List<Car> train = loco.EnumerateCoupled().ToList();
            float length = AutoEngineerPlanner.CalculateTotalLength(train);
            TrackSpan platform = stop.TrackSpans.FirstOrDefault(t => t != null);
            Vector3 at = platform != null ? platform.GetCenterPoint() : graph.GetPosition(loco.LocationF);

            // Repair tracks, scored by how many of this train's cars they work on (engine, tender, coaches:
            // worn coaches cost the railroad its passenger reputation). Farther afield when cars need it.
            bool worn = train.Any(NeedsWork);
            var shops = new List<(TrackSpan span, Industry industry, int serves, float d)>();
            foreach (RepairTrack repair in OpsController.Shared.AllIndustryComponents.OfType<RepairTrack>())
            {
                Industry industry = repair.Industry;
                if (industry == null || !industry.IsProgressionEnabled || !industry.IsTrackReachable)
                    continue;
                int serves = train.Count(c => Serves(repair, c));
                if (serves == 0)
                    continue;
                foreach (TrackSpan span in repair.TrackSpans)
                {
                    if (span == null || !span.lower.HasValue || !span.upper.HasValue || span.Length < length)
                        continue;
                    float d = Fuel.Flat(span.GetCenterPoint(), at);
                    if (d <= (worn ? RepairSearch : Search) && !Occupied(span, train))
                        shops.Add((span, industry, serves, d));
                }
            }
            if (shops.Count > 0)
            {
                var shop = shops.OrderByDescending(s => s.serves).ThenBy(s => s.d).First();
                where = "at " + shop.industry.DisplayName;
                if (shop.serves < train.Count)
                    ModEntry.Log?.Info($"Conductor: {loco.DisplayName}: the repair track at {shop.industry.DisplayName} works on {shop.serves} of its {train.Count} cars.");
                return Platforms.FarEndFor(shop.span, loco);
            }
            if (worn)
                ModEntry.Log?.Info($"Conductor: {loco.DisplayName} has cars needing repair but no open repair track nearby holds the whole train.");

            Loop siding = Turns.LoopsNear(graph, at, Search)
                .Where(l => l.Room >= length && !Occupied(graph, l, train))
                .OrderBy(l => l.Priority).ThenBy(l => Fuel.Flat(l.Center, at)).FirstOrDefault();
            if (siding != null)
            {
                where = "in the siding near " + stop.DisplayName;
                Vector3 from = graph.GetPosition(loco.LocationF);
                bool xFar = Fuel.Flat(siding.X.Position, from) > Fuel.Flat(siding.Y.Position, from);
                return xFar ? Wyes.Along(graph, siding.X, siding.AtX, Wyes.Clearance + 2f)
                            : Wyes.Along(graph, siding.Y, siding.AtY, Wyes.Clearance + 2f);
            }
            where = "at " + stop.DisplayName;
            return null;
        }

        // Whether other cars stand on a track span.
        private static bool Occupied(TrackSpan span, List<Car> train)
        {
            var cars = new List<Car>();
            TrainController.Shared.GetCarsOnSpan(span, cars);
            return cars.Any(c => !train.Contains(c));
        }

        // Whether other cars stand on a siding's track.
        private static bool Occupied(Graph graph, Loop loop, List<Car> train)
        {
            var mine = new HashSet<Car>(train);
            return TrainController.Shared.Cars.Any(c => !mine.Contains(c) && !c.IsInBardo
                && (loop.Track.Contains(c.LocationF.segment) || loop.Track.Contains(c.LocationR.segment)));
        }
    }
}
