using System.Collections.Generic;
using System.Linq;
using Model.Ops;

namespace Conductor
{
    // A station as the window lists it: where it sits on the line, and whether a train can call there yet.
    internal sealed class Station
    {
        public PassengerStop Stop;
        // 0 on the main line; 1 on a branch, listed after the junction it leaves from.
        public int Depth;

        public string Id => Stop.identifier;
        public string Name => Stop.DisplayName;

        // Why trains can't call here yet, or null when they can.
        public string Closed
        {
            get
            {
                if (!Stop.IsProgressionEnabled)
                    return "not purchased";
                if (!Stop.IsTrackReachable)
                    return "track not built";
                return null;
            }
        }
    }

    // Every passenger stop on the map, in order along the line. Worked out fresh from the game's
    // own station links each time, so stations from a newly bought expansion show up at once.
    internal static class Line
    {
        public static List<Station> Stations()
        {
            var result = new List<Station>();
            List<PassengerStop> stops = PassengerStop.FindAll()?.Where(s => s != null && !string.IsNullOrEmpty(s.identifier)).ToList();
            if (stops == null || stops.Count == 0)
                return result;
            var byId = new Dictionary<string, PassengerStop>();
            foreach (PassengerStop stop in stops)
                byId[stop.identifier] = stop;
            // The links are listed per station; make them two-way.
            var links = byId.Keys.ToDictionary(id => id, id => new HashSet<string>());
            foreach (PassengerStop stop in stops)
            {
                foreach (PassengerStop other in stop.neighbors ?? new PassengerStop[0])
                {
                    if (other == null || !byId.ContainsKey(other.identifier) || other.identifier == stop.identifier)
                        continue;
                    links[stop.identifier].Add(other.identifier);
                    links[other.identifier].Add(stop.identifier);
                }
            }
            var placed = new HashSet<string>();
            // Each connected piece of line, biggest first; on the base map there's just one.
            foreach (List<string> piece in Pieces(links).OrderByDescending(p => p.Count))
            {
                List<string> main = Longest(piece, links);
                // Sylva at the top, as the game's own maps run east to west.
                if (main.Count > 1 && main[main.Count - 1] == "sylva")
                    main.Reverse();
                var onMain = new HashSet<string>(main);
                foreach (string id in main)
                {
                    result.Add(new Station { Stop = byId[id], Depth = 0 });
                    placed.Add(id);
                    // Branches: everything reached from here without going back onto the main line.
                    foreach (string branchStart in links[id].Where(n => !onMain.Contains(n)).OrderBy(n => n))
                    {
                        foreach (string b in Walk(branchStart, links, onMain, placed))
                            result.Add(new Station { Stop = byId[b], Depth = 1 });
                    }
                }
            }
            return result;
        }

        private static List<List<string>> Pieces(Dictionary<string, HashSet<string>> links)
        {
            var seen = new HashSet<string>();
            var pieces = new List<List<string>>();
            foreach (string start in links.Keys.OrderBy(k => k))
            {
                if (!seen.Add(start))
                    continue;
                var piece = new List<string> { start };
                var queue = new Queue<string>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    foreach (string next in links[queue.Dequeue()])
                    {
                        if (seen.Add(next))
                        {
                            piece.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }
                pieces.Add(piece);
            }
            return pieces;
        }

        // The longest run of stations in a piece of line: the main line.
        private static List<string> Longest(List<string> piece, Dictionary<string, HashSet<string>> links)
        {
            string a = Farthest(piece[0], links, out _);
            string b = Farthest(a, links, out Dictionary<string, string> back);
            var path = new List<string>();
            for (string at = b; at != null; at = back[at])
                path.Add(at);
            return path;
        }

        // Breadth-first from start; back maps each station to the one before it, in the order reached.
        private static string Farthest(string start, Dictionary<string, HashSet<string>> links, out Dictionary<string, string> back)
        {
            back = new Dictionary<string, string> { [start] = null };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            string last = start;
            while (queue.Count > 0)
            {
                last = queue.Dequeue();
                foreach (string next in links[last].OrderBy(n => n))
                {
                    if (!back.ContainsKey(next))
                    {
                        back[next] = last;
                        queue.Enqueue(next);
                    }
                }
            }
            return last;
        }

        private static IEnumerable<string> Walk(string start, Dictionary<string, HashSet<string>> links, HashSet<string> onMain, HashSet<string> placed)
        {
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                string id = stack.Pop();
                if (onMain.Contains(id) || !placed.Add(id))
                    continue;
                yield return id;
                foreach (string next in links[id].OrderByDescending(n => n))
                    stack.Push(next);
            }
        }
    }
}
