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

        // How late a train can be, when Conductor is started, and still be taken up as today's.
        public const double StartLate = 30 * 60;

        // When the train is due away from a station at or after a moment (game seconds): its time
        // there on that day, or on the next day it comes round. Null when it doesn't call there.
        public static double? DepartureAfter(TT.Train train, string stopId, double after)
        {
            if (train == null)
                return null;
            int i = EntryIndex(train, stopId);
            if (i < 0 || !train.TryGetAbsoluteTimeForEntry(i, TimetableTimeType.Departure, out int minutes))
                return null;
            double at = Math.Floor(after / 86400.0) * 86400.0 + minutes * 60.0;
            while (at < after)
                at += 86400.0;
            return at;
        }

        // When the train was due in at a station, on the occurrence nearest now.
        public static double? ArrivalNear(TT.Train train, string stopId)
        {
            if (train == null)
                return null;
            int i = EntryIndex(train, stopId);
            if (i < 0)
                return null;
            if (!train.TryGetAbsoluteTimeForEntry(i, TimetableTimeType.Arrival, out int minutes) && !train.TryGetAbsoluteTimeForEntry(i, TimetableTimeType.Departure, out minutes))
                return null;
            double now = TimeWeather.Now.TotalSeconds;
            double at = Math.Floor(now / 86400.0) * 86400.0 + minutes * 60.0;
            if (at - now > 43200.0)
                at -= 86400.0;
            else if (now - at > 43200.0)
                at += 86400.0;
            return at;
        }

        // The train being run's time away from a station, counted from when it was taken up.
        public static double? Departure(Plan plan, string stopId)
        {
            TT.Train train = Find(plan.TimetableTrain);
            return plan.TimetableFrom > 0 ? DepartureAfter(train, stopId, plan.TimetableFrom) : Departure(train, stopId);
        }

        // The ticked train to run next from a station: of those that call there (and go on from it),
        // the one due away soonest at or after `after`, preferring one that starts there at the same time.
        public static TT.Train NextFrom(Plan plan, string stopId, double after, string notThis = null)
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
                double? at = DepartureAfter(t, stopId, after);
                if (at.HasValue)
                    options.Add((t, at.Value, i == 0));
            }
            return options.OrderBy(o => o.at).ThenByDescending(o => o.starts).Select(o => o.train).FirstOrDefault();
        }

        // Ticked trains don't call here: the one due away soonest from its first station.
        public static TT.Train Soonest(Plan plan, double after)
        {
            HashSet<string> ticked = plan.TimetableTrains;
            return PassengerTrains().Where(t => ticked.Contains(t.Name))
                .Select(t => (t, at: Stations(t).Count > 0 ? DepartureAfter(t, Stations(t)[0].Id, after) : null))
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
