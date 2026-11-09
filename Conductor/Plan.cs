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
        // Turning end for end on a wye at an end of the line.
        Turning,
        // Pulling into a passing siding, or waiting there, for a train coming the other way.
        Meeting,
        // Done for the night: going to where it parks, or parked there until morning.
        Layover,
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

        // At a station with a siding beside the main line, stop on the siding.
        public bool PreferSidings { get => _p["sidings"].BoolValueOrDefault(true); set => _p["sidings"] = Value.Bool(value); }

        // Look ahead for trains coming the other way and wait for them in a passing siding.
        public bool MeetTrains { get => _p["meets"].BoolValueOrDefault(true); set => _p["meets"] = Value.Bool(value); }

        // Turn the train on a wye at the ends of the line, so the engine leads.
        public bool TurnOnWyes { get => _p["wyes"].BoolValueOrDefault(true); set => _p["wyes"] = Value.Bool(value); }

        // Lay up overnight: at an end of the line after the last run's start time, everyone gets off,
        // nobody boards, and the train parks until the resume time. Times are minutes after midnight.
        public bool LayUp { get => _p["night"].BoolValue; set => _p["night"] = Value.Bool(value); }
        public int LastRunMinutes { get => _p["lastRun"].IntValueOrDefault(20 * 60 + 30); set => _p["lastRun"] = Value.Int(value); }
        public int ResumeMinutes { get => _p["resumeAt"].IntValueOrDefault(6 * 60); set => _p["resumeAt"] = Value.Int(value); }

        // Refuel when coal, water or diesel falls below this, in percent.
        public int RefuelBelow { get => _p["refuelBelow"].IntValueOrDefault(35); set => _p["refuelBelow"] = Value.Int(value); }
        public int SpeedMph { get => _p["speed"].IntValueOrDefault(35); set => _p["speed"] = Value.Int(value); }

        // Which way a run started partway along the line goes first: +1 down the list of stations,
        // -1 up it, 0 the way the train is moving or the engine faces.
        public int FirstWay { get => _p["firstWay"].IntValueOrDefault(0); set => _p["firstWay"] = Value.Int(value); }

        public bool Running { get => _p["running"].BoolValue; set => _p["running"] = Value.Bool(value); }
        // Whether Conductor runs this engine at all: on for passenger engines, off for freight and work
        // trains. Turned off mid-run, the engine holds and keeps its place until it's back on. Engines
        // set up before there was a switch count as on.
        public bool Enabled
        {
            get
            {
                Value v = _p["enabled"];
                return v.IsNull ? Running || !string.IsNullOrEmpty(_p["stops"].StringValue) : v.BoolValue;
            }
            set => _p["enabled"] = Value.Bool(value);
        }
        // Whether the runner has stood the train down for being disabled.
        public bool Held { get => _p["held"].BoolValue; set => _p["held"] = Value.Bool(value); }
        public Phase Phase { get => (Phase)_p["phase"].IntValueOrDefault(0); set => _p["phase"] = Value.Int((int)value); }
        // The station being headed for or stopped at.
        public string Station { get => _p["station"].StringValue ?? ""; set => _p["station"] = Value.String(value ?? ""); }
        // +1 down the list of stations, -1 up it.
        public int Direction { get => _p["dir"].IntValueOrDefault(1) >= 0 ? 1 : -1; set => _p["dir"] = Value.Int(value >= 0 ? 1 : -1); }
        // The fuel stop being headed for or used.
        public string Loader { get => _p["loader"].StringValue ?? ""; set => _p["loader"] = Value.String(value ?? ""); }
        // Whether the station stop has been aimed again from close by.
        public bool Aimed { get => _p["aimed"].BoolValue; set => _p["aimed"] = Value.Bool(value); }
        // Lining-up attempts at the current fuel stop.
        public int Tries { get => _p["tries"].IntValueOrDefault(0); set => _p["tries"] = Value.Int(value); }
        // A turn: its steps in order (see Turns), and which one the train is on.
        public string[] TurnMoves { get => Split(_p["turnMoves"].StringValue, ';').ToArray(); set => _p["turnMoves"] = Value.String(string.Join(";", value ?? new string[0])); }
        public int TurnStep { get => _p["turnStep"].IntValueOrDefault(0); set => _p["turnStep"] = Value.Int(value); }
        // An end of the line had no wye: turn at the next station that has one.
        public bool TurnPending { get => _p["turnPending"].BoolValue; set => _p["turnPending"] = Value.Bool(value); }
        // Whether the train has already pulled into a passing siding to turn at this station.
        public bool Parked { get => _p["parked"].BoolValue; set => _p["parked"] = Value.Bool(value); }
        // Whether the train has turned at the station it's at.
        public bool Turned { get => _p["turned"].BoolValue; set => _p["turned"] = Value.Bool(value); }

        // A meet: the train waited for, the passing siding's segments, where to stop (empty to stop
        // where it is), whether it's stopped and waiting, and the last train it couldn't meet.
        public string MeetWith { get => _p["meetWith"].StringValue ?? ""; set => _p["meetWith"] = Value.String(value ?? ""); }
        public string MeetSiding { get => _p["meetSiding"].StringValue ?? ""; set => _p["meetSiding"] = Value.String(value ?? ""); }
        public string MeetSpot { get => _p["meetSpot"].StringValue ?? ""; set => _p["meetSpot"] = Value.String(value ?? ""); }
        public bool MeetWaiting { get => _p["meetWaiting"].BoolValue; set => _p["meetWaiting"] = Value.Bool(value); }
        public string MeetWarned { get => _p["meetWarned"].StringValue ?? ""; set => _p["meetWarned"] = Value.String(value ?? ""); }

        // Laying up: decided on arriving at the end of the line; where it parks (empty to stay at the
        // station), and whether it's there.
        public bool LayingUp { get => _p["layingUp"].BoolValue; set => _p["layingUp"] = Value.Bool(value); }
        public string ParkSpot { get => _p["parkSpot"].StringValue ?? ""; set => _p["parkSpot"] = Value.String(value ?? ""); }
        public bool ParkedUp { get => _p["parkedUp"].BoolValue; set => _p["parkedUp"] = Value.Bool(value); }
        public string ParkedAt { get => _p["parkedAt"].StringValue ?? ""; set => _p["parkedAt"] = Value.String(value ?? ""); }

        // The last waypoint Conductor gave the Auto Engineer, to tell if something else changes it.
        public string Sent { get => _p["sent"].StringValue ?? ""; set => _p["sent"] = Value.String(value ?? ""); }

        // When the current phase began, in game seconds.
        public double Since { get => _p["since"].FloatValue; set => _p["since"] = Value.Float((float)value); }

        // What the train is doing, for the window and the Car tab.
        public string Status { get => _p["status"].StringValue ?? ""; set => _p["status"] = Value.String(value ?? ""); }

        private static IEnumerable<string> Split(string s, char by) =>
            string.IsNullOrEmpty(s) ? Enumerable.Empty<string>() : s.Split(by).Where(x => x.Length > 0);
    }
}
