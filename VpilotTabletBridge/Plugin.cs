using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

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
        private NotifyIcon _trayIcon;
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

            try
            {
                _server = new WebServer(port, _store, _state, _controllers, _traffic, actions);
                _server.Start();
                Log("WebServer.Start() succeeded on port " + port + ".");
            }
            catch (Exception ex)
            {
                Log("WebServer.Start() threw: " + ex);
                _broker.PostDebugMessage("[Tablet Bridge] Failed to start web server on port " + port + ": " + ex.Message);
                return;
            }

            List<string> ips = GetLocalIPv4Addresses();

            // PostDebugMessage doesn't actually reach vPilot's normal Messages
            // panel - it only goes to a separate "vPilot Debug Messages"
            // window opened with the ".debug" command, and that window only
            // shows messages posted *after* it's opened, so logging this only
            // once, immediately, is invisible unless that window happened to
            // already be open beforehand. The Windows tray notification below
            // is the primary way this is surfaced, but as a second chance for
            // anyone who opens ".debug" right after seeing that notification,
            // resend the same lines once more ~10s later - long enough to
            // realistically have typed the command by then.
            PostAddressDebugLines(ips, port);

            _delayedDebugTimer = new Timer { Interval = 10000 };
            _delayedDebugTimer.Tick += (s, e) =>
            {
                _delayedDebugTimer.Stop();
                PostAddressDebugLines(ips, port);
            };
            _delayedDebugTimer.Start();

            ShowStartupNotification(ips, port);

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
        }

        /// <summary>
        /// Pops a Windows notification balloon from the system tray with the
        /// tablet address(es), independent of vPilot's own UI entirely - see
        /// the comment where this is called for why PostDebugMessage alone
        /// isn't good enough for this.
        /// </summary>
        private void ShowStartupNotification(List<string> ips, int port)
        {
            try
            {
                var addresses = new StringBuilder();
                string firstUrl = null;
                foreach (string ip in ips)
                {
                    string url = "http://" + ip + ":" + port + "/";
                    if (firstUrl == null) firstUrl = url;
                    addresses.AppendLine(url);
                }

                _trayIcon = new NotifyIcon();
                _trayIcon.Icon = System.Drawing.SystemIcons.Information;
                _trayIcon.Visible = true;

                string text = "Tablet Bridge" + (firstUrl != null ? " - " + firstUrl : "");
                _trayIcon.Text = text.Length > 127 ? text.Substring(0, 127) : text;

                _trayIcon.BalloonTipTitle = "vPilot Tablet Bridge is running";
                _trayIcon.BalloonTipText = "Open on your tablet:" + Environment.NewLine + addresses.ToString().TrimEnd();
                _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(10000);

                Log("Tray notification shown.");
            }
            catch (Exception ex)
            {
                // Never let a notification failure take the plugin down with it.
                Log("ShowStartupNotification threw: " + ex);
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
            var results = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            results.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch
            {
                // Best effort only; fall through to the loopback fallback below.
            }

            if (results.Count == 0) results.Add("127.0.0.1");
            return results;
        }

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            _state.SetConnected(true, e.Callsign);
            _store.Add("SYSTEM", "vPilot", "Connected to network as " + e.Callsign + ".");
        }

        private void OnNetworkDisconnected(object sender, EventArgs e)
        {
            _state.SetConnected(false, null);
            _store.Add("SYSTEM", "vPilot", "Disconnected from network.");
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
            _delayedDebugTimer?.Stop();
            _delayedDebugTimer?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
    }
}
