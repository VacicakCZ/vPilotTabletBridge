using System;
using System.Threading;

using RossCarlson.Vatsim.Vpilot.Plugins;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Executes tablet-initiated actions against the real vPilot broker, and
    /// mirrors them into the message log / state so the tablet sees its own
    /// actions reflected immediately rather than waiting on a broker event.
    /// </summary>
    public class Actions
    {
        private readonly IBroker _broker;
        private readonly MessageStore _store;
        private readonly PluginState _state;

        public Actions(IBroker broker, MessageStore store, PluginState state)
        {
            _broker = broker;
            _store = store;
            _state = state;
        }

        public void Connect(string callsign, string typeCode, string selcal, string lang)
        {
            callsign = (callsign ?? "").Trim().ToUpperInvariant();
            typeCode = (typeCode ?? "").Trim().ToUpperInvariant();
            selcal = (selcal ?? "").Trim().ToUpperInvariant();

            if (callsign.Length == 0) throw new ArgumentException(Msg(lang, "Volací znak je povinný.", "Callsign is required."));
            if (typeCode.Length == 0) throw new ArgumentException(Msg(lang, "Typ letadla je povinný.", "Aircraft type is required."));

            _state.SetLastConnect(callsign, typeCode, selcal);
            _store.Add("SYSTEM", "Tablet Bridge", Msg(lang, "Připojování jako " + callsign + " (" + typeCode + ")...", "Connecting as " + callsign + " (" + typeCode + ")..."));
            _broker.RequestConnect(callsign, typeCode, selcal);
        }

        public void Disconnect(string lang)
        {
            _store.Add("SYSTEM", "Tablet Bridge", Msg(lang, "Odpojování ze sítě...", "Disconnecting from the network..."));
            _broker.RequestDisconnect();
        }

        public void RequestMetar(string station, string lang)
        {
            station = (station ?? "").Trim().ToUpperInvariant();
            if (station.Length == 0) throw new ArgumentException(Msg(lang, "Zadejte ICAO kód letiště.", "Enter an airport ICAO code."));
            _broker.RequestMetar(station);
        }

        // vPilot appears to only track one pending ATIS request at a time -
        // firing a second one before the first has had a chance to come back
        // seems to silently drop the earlier one rather than queue it
        // (observed live: requesting an airport's combined/Arrival/Departure
        // ATIS back-to-back only ever showed the last of the three). This
        // lock + minimum-interval serializes *every* RequestAtis call this
        // plugin makes - whether from the bare-ICAO expansion below or from
        // two quick taps on different Controllers-In-Range rows, which hits
        // the same problem from two separate HTTP requests/threads.
        private static readonly object _atisLock = new object();
        private static DateTime _lastAtisRequestUtc = DateTime.MinValue;
        private const int AtisMinIntervalMs = 1500;

        private void RequestAtisSingle(string callsign)
        {
            lock (_atisLock)
            {
                int wait = AtisMinIntervalMs - (int)(DateTime.UtcNow - _lastAtisRequestUtc).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
                _broker.RequestAtis(callsign);
                _lastAtisRequestUtc = DateTime.UtcNow;
            }
        }

        public void RequestAtis(string callsign, string lang)
        {
            callsign = (callsign ?? "").Trim().ToUpperInvariant();
            if (callsign.Length == 0) throw new ArgumentException(Msg(lang, "Zadejte ICAO kód letiště nebo volačku ATC stanoviště.", "Enter an airport ICAO code or an ATC station callsign."));

            if (callsign.Contains("_"))
            {
                // Already a specific callsign (e.g. a combined "LKPR_A_ATIS")
                // typed on purpose - ask for exactly that, nothing else.
                RequestAtisSingle(callsign);
                return;
            }

            // Bare ICAO code: some airports publish one combined ATIS, others
            // split it into separate Arrival/Departure ATIS under different
            // callsigns. There's no way to know which from here, so ask for
            // all three common patterns, spaced out (see RequestAtisSingle) -
            // whichever are actually staffed respond with their own
            // AtisReceived event; the rest are simply never answered, which
            // is harmless.
            string[] candidates = { callsign + "_ATIS", callsign + "_A_ATIS", callsign + "_D_ATIS" };
            int succeeded = 0;
            Exception lastError = null;
            foreach (string c in candidates)
            {
                try
                {
                    RequestAtisSingle(c);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            // Only surface an error if every attempt failed (e.g. not
            // connected at all) - a single candidate not existing is the
            // expected, silent case for whichever pattern this airport
            // doesn't use.
            if (succeeded == 0 && lastError != null) throw lastError;
        }

        public void SetRememberCallsign(bool remember, string lang)
        {
            _state.SetRememberCallsign(remember);
            _store.Add("SYSTEM", "Tablet Bridge", Msg(lang,
                "Pamatování volacího znaku: " + (remember ? "zapnuto." : "vypnuto."),
                "Remembering callsign: " + (remember ? "on." : "off.")));
        }

        public void SetModeC(bool on, string lang)
        {
            _broker.SetModeC(on);
            _store.Add("SYSTEM", "Tablet Bridge", Msg(lang, "Mode C: " + (on ? "zapnuto." : "vypnuto."), "Mode C: " + (on ? "on." : "off.")));
        }

        public void SquawkIdent(string lang)
        {
            _broker.SquawkIdent();
            _store.Add("SYSTEM", "Tablet Bridge", Msg(lang, "Odesláno IDENT.", "IDENT sent."));
        }

        public void Send(string mode, string to, string message, string lang)
        {
            message = (message ?? "").Trim();
            if (message.Length == 0) throw new ArgumentException(Msg(lang, "Prázdná zpráva.", "Empty message."));

            if (string.Equals(mode, "private", StringComparison.OrdinalIgnoreCase))
            {
                to = (to ?? "").Trim().ToUpperInvariant();
                if (to.Length == 0) throw new ArgumentException(Msg(lang, "Chybí adresát private zprávy.", "Missing recipient for the private message."));

                _broker.SendPrivateMessage(to, message);
                _store.Add("SENT_PRIVATE", Msg(lang, "Vy", "You"), message, to);
            }
            else
            {
                _broker.SendRadioMessage(message);
                _store.Add("SENT_RADIO", Msg(lang, "Vy (radio)", "You (radio)"), message);
            }
        }

        /// <summary>
        /// Picks the Czech or English variant of a server-generated
        /// system/error string based on the tablet's current UI language,
        /// sent as "lang" on every action POST (see postForm() in
        /// index.html - it's attached there once, not at each call site).
        /// Defaults to Czech - same as the client's own default - so a
        /// request that never sends it (an old cached page, or something
        /// hitting the API directly) behaves exactly as before this existed.
        /// Aviation phraseology (METAR/ATIS speech, radio content) is
        /// deliberately not routed through this - that stays English always,
        /// unrelated to the UI language toggle.
        /// </summary>
        private static string Msg(string lang, string cs, string en)
        {
            return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? en : cs;
        }
    }
}
