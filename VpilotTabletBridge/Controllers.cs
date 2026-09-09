using System;
using System.Collections.Generic;
using System.Text;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Thread-safe snapshot of ATC currently in range, built from the
    /// broker's ControllerAdded / ControllerDeleted / ControllerFrequencyChanged
    /// events - mirrors the "Controllers In Range" list in vPilot's own window.
    /// </summary>
    public class Controllers
    {
        private class Entry
        {
            public string Callsign;
            public int Frequency;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public void Add(string callsign, int frequency)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock)
            {
                _entries[callsign] = new Entry { Callsign = callsign, Frequency = frequency };
            }
        }

        public void Remove(string callsign)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock) { _entries.Remove(callsign); }
        }

        public void UpdateFrequency(string callsign, int frequency)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(callsign, out Entry e)) e.Frequency = frequency;
            }
        }

        public string ToJson()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var e in _entries.Values)
                {
                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('{');
                    sb.Append("\"callsign\":").Append(Json.Str(e.Callsign)).Append(',');
                    sb.Append("\"frequency\":").Append(Json.Str(FormatFrequency(e.Frequency))).Append(',');
                    sb.Append("\"category\":").Append(Json.Str(CategoryFor(e.Callsign)));
                    sb.Append('}');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        /// <summary>
        /// vPilot reports frequencies with the leading "1" and the decimal
        /// point stripped (123.725 -> 23725). This puts them back for display.
        /// </summary>
        private static string FormatFrequency(int freq)
        {
            string digits = freq.ToString("00000");
            if (digits.Length != 5) return freq.ToString(); // unexpected shape; show raw rather than mangle it
            return "1" + digits.Substring(0, 2) + "." + digits.Substring(2, 3);
        }

        /// <summary>
        /// Buckets a controller into the same 8 categories vPilot's own
        /// "Controllers In Range" list uses, based on the standard VATSIM
        /// callsign suffix convention.
        /// </summary>
        private static string CategoryFor(string callsign)
        {
            string cs = callsign.ToUpperInvariant();
            if (cs.EndsWith("_CTR") || cs.EndsWith("_FSS")) return "CENTER";
            if (cs.EndsWith("_APP") || cs.EndsWith("_DEP")) return "APPDEP";
            if (cs.EndsWith("_TWR")) return "TOWER";
            if (cs.EndsWith("_GND")) return "GROUND";
            if (cs.EndsWith("_RMP") || cs.EndsWith("_RAMP")) return "RAMP";
            if (cs.EndsWith("_DEL")) return "DELIVERY";
            if (cs.EndsWith("_ATIS")) return "ATIS";
            if (cs.EndsWith("_OBS")) return "OBSERVER";
            return "OTHER";
        }
    }
}
