using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game;

namespace Conductor
{
    // Conductor's own log lines, kept in memory so the window can show an engine's recent ones.
    // Everything still goes to the game's log as well.
    internal static class Journal
    {
        private const int Capacity = 1000;
        private const string Prefix = "Conductor: ";
        private static readonly LinkedList<(string clock, string text, bool warning)> _lines = new LinkedList<(string, string, bool)>();

        public static int Count { get; private set; }

        public static void Info(string message)
        {
            ModEntry.Log?.Info(message);
            Add(message, false);
        }

        public static void Warning(string message)
        {
            ModEntry.Log?.Warning(message);
            Add(message, true);
        }

        public static void Error(string message)
        {
            ModEntry.Log?.Error(message);
            Add(message, true);
        }

        private static void Add(string message, bool warning)
        {
            string text = message.StartsWith(Prefix) ? message.Substring(Prefix.Length) : message;
            string clock;
            try { clock = TimeWeather.Now.TimeString(); }
            catch { clock = ""; }
            lock (_lines)
            {
                _lines.AddLast((clock, text, warning));
                if (_lines.Count > Capacity)
                    _lines.RemoveFirst();
                Count++;
            }
        }

        // The lines that mention this engine, newest first, with its name taken off the front.
        public static string For(string engine, int max = 300)
        {
            var sb = new StringBuilder();
            int n = 0;
            lock (_lines)
            {
                for (LinkedListNode<(string clock, string text, bool warning)> node = _lines.Last; node != null && n < max; node = node.Previous)
                {
                    string text = node.Value.text;
                    if (!text.Contains(engine))
                        continue;
                    if (text.StartsWith(engine + " "))
                        text = text.Substring(engine.Length + 1);
                    text = text.Replace("<", "<​"); // no rich-text surprises from names
                    if (node.Value.warning)
                        text = "<color=#E0A040>" + text + "</color>";
                    sb.Append("<color=#9AA0A6>").Append(node.Value.clock).Append("</color>  ").Append(text).Append('\n');
                    n++;
                }
            }
            return n == 0 ? "<color=#9AA0A6>Nothing yet. Conductor's messages for this engine show here as it runs.</color>" : sb.ToString().TrimEnd('\n');
        }
    }
}
