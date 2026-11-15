using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Looks up an airport's CTAF frequency, the way the ".ctaf ICAO" command
    /// in vPilot's own window does. vPilot's plugin API has no way to run
    /// that command (see Actions.Send), and its answer is a local vPilot
    /// notification no plugin event ever sees - so the plugin does the same
    /// lookup itself: VATSIM's public AIP data, the endpoint xPilot's
    /// open-source ".ctaf" uses (my.vatsim.net, no key, read-only). It's a
    /// plain HTTPS GET from the PC, which is online anyway while vPilot is.
    /// </summary>
    internal static class CtafLookup
    {
        private const string UrlFormat = "https://my.vatsim.net/api/v2/aip/airports/{0}/stations";
        private const int TimeoutMs = 10000;

        internal class Station
        {
            public string Callsign;
            public string Name;
            public string Frequency;
        }

        /// <summary>
        /// The airport's stations flagged as CTAF (normally exactly one).
        /// Empty when the airport is unknown to VATSIM (HTTP 404) or has no
        /// CTAF. Any other failure (no internet, timeout, unexpected reply)
        /// throws.
        /// </summary>
        public static List<Station> Find(string icao)
        {
            var result = new List<Station>();

            var request = (HttpWebRequest)WebRequest.Create(string.Format(UrlFormat, Uri.EscapeDataString(icao)));
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.Accept = "application/json";
            request.UserAgent = "VpilotTabletBridge";

            string json;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound)
            {
                ex.Response.Close();
                return result; // {"message":"airport could not be found"}
            }

            // {"data":[{"callsign":"SAN_TWR","name":"Lindbergh Tower","frequency":"118.300","ctaf":true}, ...]}
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("data", out object data) || !(data is IEnumerable items))
            {
                throw new InvalidDataException("Unexpected AIP reply.");
            }

            foreach (object item in items)
            {
                var station = item as Dictionary<string, object>;
                if (station == null) continue;
                if (!(station.TryGetValue("ctaf", out object ctaf) && ctaf is bool isCtaf && isCtaf)) continue;

                string frequency = Field(station, "frequency");
                if (frequency.Length == 0) continue;
                result.Add(new Station
                {
                    Callsign = Field(station, "callsign"),
                    Name = Field(station, "name"),
                    Frequency = frequency
                });
            }
            return result;
        }

        private static string Field(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Trim() : "";
        }
    }
}
