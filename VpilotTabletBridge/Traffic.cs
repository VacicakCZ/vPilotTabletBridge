using System;
using System.Collections.Generic;
using System.Text;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Thread-safe snapshot of nearby aircraft, built from the broker's
    /// AircraftAdded / AircraftUpdated / AircraftDeleted events.
    ///
    /// There is no way to get the plugin's own aircraft position from
    /// vPilot's API, so a real distance or bearing from the pilot can't be
    /// computed or sorted by here - this list is simply whatever vPilot
    /// itself is currently modeling, which vPilot already limits to nearby
    /// traffic on its own before these events ever reach a plugin.
    /// </summary>
    public class Traffic
    {
        private class Entry
        {
            public string Callsign;
            public string TypeCode;
            public double Altitude;
            public double Heading;
            public double Speed;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public void Add(string callsign, string typeCode, double altitude, double heading, double speed)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock)
            {
                _entries[callsign] = new Entry
                {
                    Callsign = callsign,
                    TypeCode = typeCode,
                    Altitude = altitude,
                    Heading = heading,
                    Speed = speed
                };
            }
        }

        public void Update(string callsign, double altitude, double heading, double speed)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(callsign, out Entry e))
                {
                    e.Altitude = altitude;
                    e.Heading = heading;
                    e.Speed = speed;
                }
            }
        }

        public void Remove(string callsign)
        {
            if (string.IsNullOrEmpty(callsign)) return;
            lock (_lock) { _entries.Remove(callsign); }
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
                    sb.Append("\"typeCode\":").Append(Json.Str(e.TypeCode ?? "")).Append(',');
                    sb.Append("\"altitude\":").Append((long)Math.Round(e.Altitude)).Append(',');
                    sb.Append("\"heading\":").Append((long)Math.Round(e.Heading)).Append(',');
                    sb.Append("\"speed\":").Append((long)Math.Round(e.Speed));
                    sb.Append('}');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }
    }
}
