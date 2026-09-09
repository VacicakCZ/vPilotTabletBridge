using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Tracks connection state for the tablet page, plus the last-used
    /// connect details (persisted to disk so the tablet's connect form
    /// starts pre-filled instead of blank every time).
    ///
    /// Connected/Callsign is always accurate: NetworkConnected/NetworkDisconnected
    /// fire no matter which UI (vPilot's own window or this page) triggered
    /// the connect/disconnect.
    ///
    /// Whether the callsign specifically gets remembered is a user setting
    /// (RememberCallsign) - useful if the tablet/cockpit is shared between
    /// different callsigns. Aircraft type and SELCAL are always remembered;
    /// only the callsign has this opt-out, per what was actually asked for.
    /// </summary>
    public class PluginState
    {
        private readonly object _lock = new object();

        private bool _connected;
        private string _callsign;
        private DateTime? _connectedSinceUtc;

        private bool _rememberCallsign = true;
        private string _lastCallsign = "";
        private string _lastTypeCode = "";
        private string _lastSelcal = "";

        private readonly string _prefillPath;

        public PluginState()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            _prefillPath = Path.Combine(dir, "TabletBridge-connect.ini");
            LoadPrefill();
        }

        public void SetConnected(bool connected, string callsign)
        {
            lock (_lock)
            {
                _connected = connected;
                _callsign = connected ? callsign : null;
                _connectedSinceUtc = connected ? DateTime.UtcNow : (DateTime?)null;
            }
        }

        public void SetLastConnect(string callsign, string typeCode, string selcal)
        {
            lock (_lock)
            {
                _lastCallsign = _rememberCallsign ? (callsign ?? "") : "";
                _lastTypeCode = typeCode ?? "";
                _lastSelcal = selcal ?? "";
            }
            SavePrefill();
        }

        public void SetRememberCallsign(bool remember)
        {
            lock (_lock)
            {
                _rememberCallsign = remember;
                if (!remember) _lastCallsign = ""; // forget immediately, don't wait for the next connect
            }
            SavePrefill();
        }

        public string ToJson()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append("\"connected\":").Append(Json.Bool(_connected)).Append(',');
                sb.Append("\"callsign\":").Append(Json.Str(_callsign)).Append(',');
                sb.Append("\"connectedSinceUtc\":").Append(_connectedSinceUtc.HasValue ? Json.Str(_connectedSinceUtc.Value.ToString("o")) : "null").Append(',');
                sb.Append("\"rememberCallsign\":").Append(Json.Bool(_rememberCallsign)).Append(',');
                sb.Append("\"lastCallsign\":").Append(Json.Str(_lastCallsign)).Append(',');
                sb.Append("\"lastTypeCode\":").Append(Json.Str(_lastTypeCode)).Append(',');
                sb.Append("\"lastSelcal\":").Append(Json.Str(_lastSelcal));
                sb.Append('}');
                return sb.ToString();
            }
        }

        private void LoadPrefill()
        {
            try
            {
                if (!File.Exists(_prefillPath)) return;

                foreach (string rawLine in File.ReadAllLines(_prefillPath))
                {
                    string line = rawLine.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key.Equals("Callsign", StringComparison.OrdinalIgnoreCase)) _lastCallsign = value;
                    else if (key.Equals("TypeCode", StringComparison.OrdinalIgnoreCase)) _lastTypeCode = value;
                    else if (key.Equals("Selcal", StringComparison.OrdinalIgnoreCase)) _lastSelcal = value;
                    else if (key.Equals("RememberCallsign", StringComparison.OrdinalIgnoreCase))
                    {
                        bool.TryParse(value, out _rememberCallsign);
                    }
                }
            }
            catch
            {
                // Ignore malformed/missing prefill file; the form just starts blank.
            }
        }

        private void SavePrefill()
        {
            try
            {
                string content =
                    "Callsign=" + _lastCallsign + Environment.NewLine +
                    "TypeCode=" + _lastTypeCode + Environment.NewLine +
                    "Selcal=" + _lastSelcal + Environment.NewLine +
                    "RememberCallsign=" + _rememberCallsign + Environment.NewLine;
                File.WriteAllText(_prefillPath, content);
            }
            catch
            {
                // Non-fatal: worst case the form isn't pre-filled next time.
            }
        }
    }
}
