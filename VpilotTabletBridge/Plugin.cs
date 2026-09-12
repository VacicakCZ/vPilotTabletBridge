using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace VpilotTabletBridge
{
    /// <summary>
    /// vPilot plugin that mirrors radio/private/broadcast/SELCAL messages and
    /// connect/disconnect notices to a small web page served over the LAN,
    /// so they can be read on a tablet in a home cockpit where the PC itself
    /// is out of reach.
    /// </summary>
    public class Plugin : IPlugin
    {
        private const int DefaultPort = 8686;

        public string Name => "Tablet Bridge";

        private IBroker _broker;
        private WebServer _server;
        private readonly MessageStore _store = new MessageStore();
        private readonly PluginState _state = new PluginState();
        private readonly Controllers _controllers = new Controllers();
        private readonly Traffic _traffic = new Traffic();
        private Timer _delayedDebugTimer;

        public void Initialize(IBroker broker)
        {
            // Written straight to disk, independent of vPilot's own UI/broker,
            // so we have ground truth even if something fails before we can
            // call PostDebugMessage, or if the message panel doesn't surface it.
            Log("Initialize() called.");
            try
            {
                InitializeCore(broker);
                Log("Initialize() completed normally.");
            }
            catch (Exception ex)
            {
                Log("Initialize() threw: " + ex);
                throw;
            }
        }

        private void InitializeCore(IBroker broker)
        {
            _broker = broker;

            int port = LoadPort();
            Log("Using port " + port + ".");

            var actions = new Actions(broker, _store, _state);
            List<string> ips = GetLocalIPv4Addresses();

            try
            {
                _server = new WebServer(port, _store, _state, _controllers, _traffic, actions, ips);
                _server.Start();
                Log("WebServer.Start() succeeded on port " + port + ".");
            }
            catch (Exception ex)
            {
                Log("WebServer.Start() threw: " + ex);
                _broker.PostDebugMessage("[Tablet Bridge] Failed to start web server on port " + port + ": " + ex.Message);
                return;
            }

            // PostDebugMessage doesn't reach vPilot's normal Messages panel -
            // it only goes to the separate "vPilot Debug Messages" window
            // opened with the ".debug" command, and that window only shows
            // messages posted *after* it's opened. So: post immediately (in
            // case it's already open), and post again ~10s later, which is
            // there to actually land if ".debug" gets opened right after
            // vPilot starts rather than before - see the README for the
            // full explanation and the TabletBridge-debug.log fallback.
            PostAddressDebugLines(ips, port);
            _delayedDebugTimer = new Timer(delegate
            {
                PostAddressDebugLines(ips, port);
                _delayedDebugTimer.Dispose();
            }, null, 10000, Timeout.Infinite);

            OpenQrCodeOnFirstRunOnly(ips, port);

            _broker.NetworkConnected += OnNetworkConnected;
            _broker.NetworkDisconnected += OnNetworkDisconnected;
            _broker.RadioMessageReceived += OnRadioMessageReceived;
            _broker.PrivateMessageReceived += OnPrivateMessageReceived;
            _broker.BroadcastMessageReceived += OnBroadcastMessageReceived;
            _broker.SelcalAlertReceived += OnSelcalAlertReceived;
            _broker.MetarReceived += OnMetarReceived;
            _broker.AtisReceived += OnAtisReceived;
            _broker.ControllerAdded += OnControllerAdded;
            _broker.ControllerDeleted += OnControllerDeleted;
            _broker.ControllerFrequencyChanged += OnControllerFrequencyChanged;
            _broker.AircraftAdded += OnAircraftAdded;
            _broker.AircraftUpdated += OnAircraftUpdated;
            _broker.AircraftDeleted += OnAircraftDeleted;
            _broker.SessionEnded += OnSessionEnded;

            // Deliberately not added to _store: this fires on every vPilot
            // restart, and a pilot re-opening the tablet mid-session doesn't
            // need to see it - it's still visible in vPilot's own debug
            // console above, and in TabletBridge-debug.log, for troubleshooting.
            Log("Event subscriptions done.");
        }

        private void PostAddressDebugLines(List<string> ips, int port)
        {
            _broker.PostDebugMessage("[Tablet Bridge] Running. Open one of these addresses on your tablet:");
            foreach (string ip in ips)
            {
                _broker.PostDebugMessage("[Tablet Bridge]   http://" + ip + ":" + port + "/");
            }
            if (ips.Count > 0)
            {
                _broker.PostDebugMessage("[Tablet Bridge] Or scan a QR code for it: http://" + ips[0] + ":" + port + "/qr");
            }
        }

        /// <summary>
        /// Opens the QR-code connect page in the default browser, but only
        /// the very first time this plugin ever loads - a marker file next
        /// to the DLL remembers that it already happened, so it never pops
        /// up again on every subsequent vPilot start (which would be the
        /// same kind of unwanted repeat-notification the tray-icon approach
        /// was dropped for earlier in this project). ".debug"/the log file
        /// keep mentioning the /qr URL every time for whoever wants to see
        /// it again later.
        /// </summary>
        private void OpenQrCodeOnFirstRunOnly(List<string> ips, int port)
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string markerPath = Path.Combine(dir, "TabletBridge-qr-shown.flag");
                if (File.Exists(markerPath)) return;

                File.WriteAllText(markerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if (ips.Count == 0) return;

                Process.Start(new ProcessStartInfo("http://" + ips[0] + ":" + port + "/qr") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("OpenQrCodeOnFirstRunOnly() threw: " + ex);
                // Non-fatal: worst case the pilot just doesn't get the popup
                // and finds the address in .debug/the log file instead.
            }
        }

        /// <summary>
        /// Small on-disk trace log (TabletBridge-debug.log next to the plugin
        /// DLL) for diagnosing load/startup issues independent of whatever
        /// vPilot itself does with PostDebugMessage output. Never throws.
        /// </summary>
        private static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string path = Path.Combine(dir, "TabletBridge-debug.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never be the reason the plugin misbehaves.
            }
        }

        /// <summary>
        /// Reads an optional "Port=NNNN" line from TabletBridge.ini next to the
        /// plugin DLL (i.e. in vPilot's Plugins folder), falling back to 8686.
        /// Kept as a hand-rolled one-line parser to avoid pulling in any INI
        /// library the plugin doesn't otherwise need.
        /// </summary>
        private int LoadPort()
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string iniPath = Path.Combine(pluginDir ?? "", "TabletBridge.ini");
                if (File.Exists(iniPath))
                {
                    foreach (string rawLine in File.ReadAllLines(iniPath))
                    {
                        string line = rawLine.Trim();
                        if (line.StartsWith("Port", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] kv = line.Split('=');
                            if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out int parsed) && parsed > 0 && parsed < 65536)
                            {
                                return parsed;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Malformed or unreadable config: fall back to the default port.
            }
            return DefaultPort;
        }

        private static List<string> GetLocalIPv4Addresses()
        {
            var candidates = new List<CandidateAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    bool likelyVirtual = IsLikelyVirtualAdapter(ni);

                    foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            candidates.Add(new CandidateAddress { Ip = addr.Address.ToString(), LikelyVirtual = likelyVirtual });
                        }
                    }
                }
            }
            catch
            {
                // Best effort only; fall through to the loopback fallback below.
            }

            if (candidates.Count == 0) candidates.Add(new CandidateAddress { Ip = "127.0.0.1", LikelyVirtual = false });

            // NetworkInterface.GetAllNetworkInterfaces() doesn't order these
            // usefully - a Tailscale/other VPN adapter (100.64.0.0/10) or a
            // WSL/Hyper-V virtual switch can easily land ahead of the actual
            // home-LAN address a tablet on the same Wi-Fi can reach. Only
            // matters cosmetically for the plain text list, but it matters a
            // lot for the QR code, which can only encode one address - so
            // sort real home-LAN ranges first, and push anything that *looks*
            // virtual (by adapter name) to the back even if its IP happens to
            // land in a private range too - VirtualBox's default host-only
            // adapter is 192.168.56.x, Hyper-V's "Default Switch" is commonly
            // a 172.x address, either of which would otherwise tie with a
            // genuine home-LAN address of the same class and could easily win
            // the tie (a user reported exactly this symptom: the shipped DLL
            // "only worked on the developer's machine" until they rebuilt -
            // almost certainly them hitting the pre-sort/pre-this-check
            // version of this method, not a build-time/machine-baked address).
            candidates.Sort((a, b) => CombinedPriority(a).CompareTo(CombinedPriority(b)));

            var results = new List<string>();
            foreach (CandidateAddress c in candidates) results.Add(c.Ip);

            // Escape hatch for the rare case where the heuristic above still
            // guesses wrong: an optional "IP=x.x.x.x" line in TabletBridge.ini
            // (same file/format as Port=, see LoadPort) forces that address
            // to the front - i.e. what the QR code and .debug/log output lead
            // with - without turning off automatic detection for the rest of
            // the list (still used for the "Other addresses" fallback on the
            // /qr page).
            string overrideIp = LoadIpOverride();
            if (overrideIp != null)
            {
                results.Remove(overrideIp);
                results.Insert(0, overrideIp);
            }

            return results;
        }

        private class CandidateAddress
        {
            public string Ip;
            public bool LikelyVirtual;
        }

        private static int CombinedPriority(CandidateAddress c)
        {
            int basePriority = AddressPriority(c.Ip);
            return c.LikelyVirtual ? basePriority + 10 : basePriority;
        }

        private static int AddressPriority(string ip)
        {
            byte[] parts;
            try { parts = System.Net.IPAddress.Parse(ip).GetAddressBytes(); }
            catch { return 3; }
            if (parts.Length != 4) return 3;

            if (parts[0] == 192 && parts[1] == 168) return 0;
            if (parts[0] == 10) return 1;
            if (parts[0] == 172 && parts[1] >= 16 && parts[1] <= 31) return 2;
            return 3; // includes Tailscale's 100.64.0.0/10 and anything else
        }

        /// <summary>
        /// Best-effort check for adapters that are virtual/tunnel interfaces
        /// rather than a physical link to the home LAN, by name/description
        /// rather than by IP range - a numeric range alone can't tell a real
        /// home-LAN address apart from e.g. a VirtualBox host-only adapter,
        /// which also defaults to a 192.168.x.x address.
        /// </summary>
        private static bool IsLikelyVirtualAdapter(NetworkInterface ni)
        {
            string text = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            string[] markers =
            {
                "virtualbox", "vmware", "hyper-v", "virtual switch", "wsl",
                "docker", "tailscale", "zerotier", "tap-windows", "tap adapter"
            };
            foreach (string marker in markers)
            {
                if (text.Contains(marker)) return true;
            }
            return false;
        }

        /// <summary>
        /// Reads an optional "IP=x.x.x.x" line from TabletBridge.ini next to
        /// the plugin DLL (same file as Port=, see LoadPort) - an escape
        /// hatch for whoever the automatic detection above still picks the
        /// wrong address for. When present and a syntactically valid IPv4
        /// address, GetLocalIPv4Addresses moves it to the front of the
        /// detected list rather than replacing the list outright, so the
        /// rest of the detected addresses are still available as a fallback.
        /// </summary>
        private static string LoadIpOverride()
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string iniPath = Path.Combine(pluginDir ?? "", "TabletBridge.ini");
                if (!File.Exists(iniPath)) return null;

                foreach (string rawLine in File.ReadAllLines(iniPath))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("IP", StringComparison.OrdinalIgnoreCase)) continue;

                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;

                    string candidate = kv[1].Trim();
                    if (System.Net.IPAddress.TryParse(candidate, out System.Net.IPAddress parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return parsed.ToString();
                    }
                }
            }
            catch
            {
                // Malformed or unreadable config: fall back to automatic detection only.
            }
            return null;
        }

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            _state.SetConnected(true, e.Callsign);
            _store.Add("SYSTEM", "vPilot", _state.Localize(
                "Připojen k síti jako " + e.Callsign + ".",
                "Connected to network as " + e.Callsign + "."));
        }

        private void OnNetworkDisconnected(object sender, EventArgs e)
        {
            _state.SetConnected(false, null);
            _store.Add("SYSTEM", "vPilot", _state.Localize("Odpojen od sítě.", "Disconnected from network."));
        }

        private void OnRadioMessageReceived(object sender, RadioMessageReceivedEventArgs e)
        {
            _store.Add("RADIO", e.From, e.Message);
        }

        private void OnPrivateMessageReceived(object sender, PrivateMessageReceivedEventArgs e)
        {
            _store.Add("PRIVATE", e.From, e.Message, e.From);
        }

        private void OnBroadcastMessageReceived(object sender, BroadcastMessageReceivedEventArgs e)
        {
            _store.Add("BROADCAST", e.From, e.Message);
        }

        private void OnSelcalAlertReceived(object sender, SelcalAlertReceivedEventArgs e)
        {
            _store.Add("SELCAL", e.From, "SELCAL alert received.");
        }

        private void OnMetarReceived(object sender, MetarReceivedEventArgs e)
        {
            _store.Add("METAR", "METAR", e.Metar);
        }

        private void OnAtisReceived(object sender, AtisReceivedEventArgs e)
        {
            _store.Add("ATIS", e.From, string.Join(Environment.NewLine, e.Lines));
        }

        private void OnControllerAdded(object sender, ControllerAddedEventArgs e)
        {
            _controllers.Add(e.Callsign, e.Frequency);
        }

        private void OnControllerDeleted(object sender, ControllerDeletedEventArgs e)
        {
            _controllers.Remove(e.Callsign);
        }

        private void OnControllerFrequencyChanged(object sender, ControllerFrequencyChangedEventArgs e)
        {
            _controllers.UpdateFrequency(e.Callsign, e.NewFrequency);
        }

        private void OnAircraftAdded(object sender, AircraftAddedEventArgs e)
        {
            _traffic.Add(e.Callsign, e.TypeCode, e.Altitude, e.Heading, e.Speed);
        }

        private void OnAircraftUpdated(object sender, AircraftUpdatedEventArgs e)
        {
            _traffic.Update(e.Callsign, e.Altitude, e.Heading, e.Speed);
        }

        private void OnAircraftDeleted(object sender, AircraftDeletedEventArgs e)
        {
            _traffic.Remove(e.Callsign);
        }

        private void OnSessionEnded(object sender, EventArgs e)
        {
            _server?.Stop();
            _delayedDebugTimer?.Dispose();
        }
    }
}
