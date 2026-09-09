using System;
using System.Collections.Generic;
using System.Text;

namespace VpilotTabletBridge
{
    /// <summary>
    /// One line in the tablet log: a radio/private/broadcast/SELCAL message
    /// or a system notice (connected/disconnected/plugin started).
    /// </summary>
    public class MessageEntry
    {
        public long Id;
        public DateTime TimeUtc;
        public string Type;   // RADIO, PRIVATE, BROADCAST, SELCAL, SYSTEM
        public string From;
        public string Text;
    }

    /// <summary>
    /// Thread-safe, capped in-memory log of recent messages.
    /// vPilot raises broker events on its UI thread; the web server reads
    /// this from its own accept/worker threads, so every access is locked.
    /// </summary>
    public class MessageStore
    {
        private const int MaxEntries = 300;

        private readonly object _lock = new object();
        private readonly LinkedList<MessageEntry> _entries = new LinkedList<MessageEntry>();
        private long _nextId = 1;

        public void Add(string type, string from, string text)
        {
            lock (_lock)
            {
                _entries.AddLast(new MessageEntry
                {
                    Id = _nextId++,
                    TimeUtc = DateTime.UtcNow,
                    Type = type ?? "SYSTEM",
                    From = from ?? "",
                    Text = text ?? ""
                });

                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveFirst();
                }
            }
        }

        /// <summary>Serializes all currently stored messages (oldest first) as a JSON array.</summary>
        public string ToJson()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var m in _entries)
                {
                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('{');
                    sb.Append("\"id\":").Append(m.Id).Append(',');
                    sb.Append("\"time\":\"").Append(m.TimeUtc.ToLocalTime().ToString("HH:mm:ss")).Append("\",");
                    sb.Append("\"type\":").Append(Json.Str(m.Type)).Append(',');
                    sb.Append("\"from\":").Append(Json.Str(m.From)).Append(',');
                    sb.Append("\"text\":").Append(Json.Str(m.Text));
                    sb.Append('}');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }
    }
}
