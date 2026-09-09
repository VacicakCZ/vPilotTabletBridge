using System;

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

        public void Connect(string callsign, string typeCode, string selcal)
        {
            callsign = (callsign ?? "").Trim().ToUpperInvariant();
            typeCode = (typeCode ?? "").Trim().ToUpperInvariant();
            selcal = (selcal ?? "").Trim().ToUpperInvariant();

            if (callsign.Length == 0) throw new ArgumentException("Volací znak je povinný.");
            if (typeCode.Length == 0) throw new ArgumentException("Typ letadla je povinný.");

            _state.SetLastConnect(callsign, typeCode, selcal);
            _store.Add("SYSTEM", "Tablet Bridge", "Připojování jako " + callsign + " (" + typeCode + ")...");
            _broker.RequestConnect(callsign, typeCode, selcal);
        }

        public void Disconnect()
        {
            _store.Add("SYSTEM", "Tablet Bridge", "Odpojování ze sítě...");
            _broker.RequestDisconnect();
        }

        public void RequestMetar(string station)
        {
            station = (station ?? "").Trim().ToUpperInvariant();
            if (station.Length == 0) throw new ArgumentException("Zadejte ICAO kód letiště.");
            _broker.RequestMetar(station);
        }

        public void RequestAtis(string callsign)
        {
            callsign = (callsign ?? "").Trim().ToUpperInvariant();
            if (callsign.Length == 0) throw new ArgumentException("Zadejte ICAO kód letiště nebo volačku ATC stanoviště.");

            if (callsign.Contains("_"))
            {
                // Already a specific callsign (e.g. a combined "LKPR_A_ATIS")
                // typed on purpose - ask for exactly that, nothing else.
                _broker.RequestAtis(callsign);
                return;
            }

            // Bare ICAO code: some airports publish one combined ATIS, others
            // split it into separate Arrival/Departure ATIS under different
            // callsigns. There's no way to know which from here, so ask for
            // all three common patterns - whichever are actually staffed
            // respond with their own AtisReceived event; the rest are simply
            // never answered, which is harmless.
            string[] candidates = { callsign + "_ATIS", callsign + "_A_ATIS", callsign + "_D_ATIS" };
            int succeeded = 0;
            Exception lastError = null;
            foreach (string c in candidates)
            {
                try
                {
                    _broker.RequestAtis(c);
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

        public void SetRememberCallsign(bool remember)
        {
            _state.SetRememberCallsign(remember);
            _store.Add("SYSTEM", "Tablet Bridge", "Pamatování volacího znaku: " + (remember ? "zapnuto." : "vypnuto."));
        }

        public void Send(string mode, string to, string message)
        {
            message = (message ?? "").Trim();
            if (message.Length == 0) throw new ArgumentException("Prázdná zpráva.");

            if (string.Equals(mode, "private", StringComparison.OrdinalIgnoreCase))
            {
                to = (to ?? "").Trim().ToUpperInvariant();
                if (to.Length == 0) throw new ArgumentException("Chybí adresát private zprávy.");

                _broker.SendPrivateMessage(to, message);
                _store.Add("SENT_PRIVATE", "Vy", message, to);
            }
            else
            {
                _broker.SendRadioMessage(message);
                _store.Add("SENT_RADIO", "Vy (radio)", message);
            }
        }
    }
}
