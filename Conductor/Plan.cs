using System.Collections.Generic;
using System.Linq;
using Helpers;
using KeyValue.Runtime;
using Model;

namespace Conductor
{
    // Where a run is.
    internal enum Phase
    {
        Idle,
        // Heading for the next station.
        ToStation,
        // Stopped at a station while passengers get off and on.
        AtStation,
        // Heading for a fuel stop, or lining the tender up under it.
        ToFuel,
        // Stopped under a spout, filling.
        Fueling,
    }

    // A fuel stop: one water spout, coal chute or diesel stand.
    internal struct FuelStop
    {
        // The loader's registered id, which the game keeps from save to save.
        public string LoaderId;
        public string LoadId;
    }

    // An engine's Conductor settings and run, kept in the engine's own properties ("conductor"),
    // so they're saved with the game and shared with everyone in multiplayer.
    internal readonly struct Plan
    {
        private const string Key = "conductor";
        private readonly KeyValueProperties _p;

        public Plan(Car loco)
        {
            _p = new KeyValueProperties(loco.KeyValueObject, Key);
        }

        public bool AuthorizedToWrite => _p.AuthorizedToWrite;

        // The stations the train calls at, by id.
        public HashSet<string> Stops
        {
            get => new HashSet<string>(Split(_p["stops"].StringValue, ','));
            set => _p["stops"] = Value.String(string.Join(",", value ?? new HashSet<string>()));
        }

        public List<FuelStop> FuelStops
        {
            get => Split(_p["fuel"].StringValue, ';').Select(s => s.Split('|')).Where(p => p.Length == 2)
                .Select(p => new FuelStop { LoaderId = p[0], LoadId = p[1] }).ToList();
            set => _p["fuel"] = Value.String(string.Join(";", (value ?? new List<FuelStop>()).Select(f => f.LoaderId + "|" + f.LoadId)));
        }

        // Use any open fuel stop on the railroad, rather than only the ones on the list.
        public bool AnyFuelStop { get => _p["anyFuel"].BoolValueOrDefault(true); set => _p["anyFuel"] = Value.Bool(value); }

        // Refuel when coal, water or diesel falls below this, in percent.
        public int RefuelBelow { get => _p["refuelBelow"].IntValueOrDefault(35); set => _p["refuelBelow"] = Value.Int(value); }
        public int SpeedMph { get => _p["speed"].IntValueOrDefault(35); set => _p["speed"] = Value.Int(value); }

        public bool Running { get => _p["running"].BoolValue; set => _p["running"] = Value.Bool(value); }
        public Phase Phase { get => (Phase)_p["phase"].IntValueOrDefault(0); set => _p["phase"] = Value.Int((int)value); }
        // The station being headed for or stopped at.
        public string Station { get => _p["station"].StringValue ?? ""; set => _p["station"] = Value.String(value ?? ""); }
        // +1 down the list of stations, -1 up it.
        public int Direction { get => _p["dir"].IntValueOrDefault(1) >= 0 ? 1 : -1; set => _p["dir"] = Value.Int(value >= 0 ? 1 : -1); }
        // The fuel stop being headed for or used.
        public string Loader { get => _p["loader"].StringValue ?? ""; set => _p["loader"] = Value.String(value ?? ""); }
        // Lining-up attempts at the current fuel stop.
        public int Tries { get => _p["tries"].IntValueOrDefault(0); set => _p["tries"] = Value.Int(value); }
        // When the current phase began, in game seconds.
        public double Since { get => _p["since"].FloatValue; set => _p["since"] = Value.Float((float)value); }

        // What the train is doing, for the window and the Car tab.
        public string Status { get => _p["status"].StringValue ?? ""; set => _p["status"] = Value.String(value ?? ""); }

        private static IEnumerable<string> Split(string s, char by) =>
            string.IsNullOrEmpty(s) ? Enumerable.Empty<string>() : s.Split(by).Where(x => x.Length > 0);
    }
}
