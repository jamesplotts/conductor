using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Model.Ops;
using Model.Ops.Timetable;
using TT = Model.Ops.Timetable.Timetable;

namespace Conductor
{
    // The game's own timetable (Timetable window): its passenger trains, their stations in running
    // order, and when they're due away from each. In Timetable mode Conductor runs the trains ticked
    // for an engine, one after another, instead of shuttling between the ends of the line.
    internal static class Timetables
    {
        // How late a train can be and still count as today's, in seconds.
        private const double LateAllowance = 3 * 3600;

        public static TT Current => TimetableController.Shared?.Current;

        public static List<TT.Train> PassengerTrains()
        {
            TT tt = Current;
            if (tt?.Trains == null)
                return new List<TT.Train>();
            return tt.Trains.Values.Where(t => t.TrainType == TT.TrainType.Passenger && t.Entries.Count >= 2)
                .OrderBy(t => t.SortOrderWithinClass).ThenBy(t => t.SortName).ToList();
        }

        public static TT.Train Find(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return null;
            TT tt = Current;
            return tt?.Trains != null && tt.Trains.TryGetValue(symbol, out TT.Train t) ? t : null;
        }

        public static PassengerStop StopFor(string code) =>
            TimetableController.Shared != null && TimetableController.Shared.TryGetPassengerStop(code, out PassengerStop stop) ? stop : null;

        // The train's stations in the order it calls at them (stations with no passenger stop left out).
        public static List<Station> Stations(TT.Train train)
        {
            var list = new List<Station>();
            if (train == null)
                return list;
            foreach (TT.Entry e in train.Entries)
            {
                PassengerStop stop = StopFor(e.Station);
                if (stop != null && list.All(s => s.Id != stop.identifier))
                    list.Add(new Station { Stop = stop });
            }
            return list;
        }

        private static int EntryIndex(TT.Train train, string stopId)
        {
            for (int i = 0; i < train.Entries.Count; i++)
                if (StopFor(train.Entries[i].Station)?.identifier == stopId)
                    return i;
            return -1;
        }

        // When the train is due away from a station, in game seconds: the next time it comes round,
        // or earlier today if it's late by less than LateAllowance. Null when it doesn't call there.
        public static double? Departure(TT.Train train, string stopId)
        {
            if (train == null)
                return null;
            int i = EntryIndex(train, stopId);
            if (i < 0)
                return null;
            GameDateTime now = TimeWeather.Now;
            if (!train.TryGetAbsoluteTimeForEntry(i, TimetableTimeType.Departure, out int minutes))
                return null;
            double day = Math.Floor(now.TotalSeconds / 86400.0) * 86400.0;
            double at = day + minutes * 60.0;
            if (at < now.TotalSeconds - LateAllowance)
                at += 86400.0;
            else if (at > now.TotalSeconds + 86400.0 - LateAllowance)
                at -= 86400.0;
            return at;
        }

        public static string Clock(double gameSeconds) => Layover.Clock((int)((long)(gameSeconds / 60.0) % (24 * 60)));

        // The ticked train to run next from a station: of those that call there (and go on from it),
        // the one due away soonest. Prefers trains that start there.
        public static TT.Train NextFrom(Plan plan, string stopId, string notThis = null)
        {
            HashSet<string> ticked = plan.TimetableTrains;
            var options = new List<(TT.Train train, double at, bool starts)>();
            foreach (TT.Train t in PassengerTrains())
            {
                if (!ticked.Contains(t.Name) || t.Name == notThis)
                    continue;
                List<Station> stations = Stations(t);
                int i = stations.FindIndex(s => s.Id == stopId);
                if (i < 0 || i == stations.Count - 1)
                    continue;
                double? at = Departure(t, stopId);
                if (at.HasValue)
                    options.Add((t, at.Value, i == 0));
            }
            return options.OrderByDescending(o => o.starts).ThenBy(o => o.at).Select(o => o.train).FirstOrDefault();
        }

        // Ticked trains don't call here: the one due away soonest from its first station.
        public static TT.Train Soonest(Plan plan)
        {
            HashSet<string> ticked = plan.TimetableTrains;
            return PassengerTrains().Where(t => ticked.Contains(t.Name))
                .Select(t => (t, at: Stations(t).Count > 0 ? Departure(t, Stations(t)[0].Id) : null))
                .Where(x => x.at.HasValue).OrderBy(x => x.at.Value).Select(x => x.t).FirstOrDefault();
        }

        public static string Describe(TT.Train t)
        {
            List<Station> s = Stations(t);
            if (s.Count < 2)
                return t.DisplayStringShort;
            double? a = Departure(t, s[0].Id);
            return $"{t.DisplayStringShort}: {s[0].Name}{(a.HasValue ? " " + Clock(a.Value) : "")} to {s[s.Count - 1].Name}";
        }
    }
}
