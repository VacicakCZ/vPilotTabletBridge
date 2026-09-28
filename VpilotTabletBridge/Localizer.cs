using System;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Picks the variant of a server-generated system/error string that
    /// matches the tablet page's UI language - the one place the plugin's
    /// languages are listed server-side (see I18N in index.html for the
    /// client-side half). Czech and English are the reference languages
    /// every message must have; German, Spanish and Chinese are optional
    /// per message and fall back to English when a call site doesn't supply
    /// them.
    /// </summary>
    internal static class Localizer
    {
        /// <summary>
        /// <paramref name="lang"/> is the code the tablet page sends
        /// ("cs", "en", "de", "es", "zh" - a region suffix such as "zh-CN" is
        /// ignored). Empty/missing means a caller from before the language
        /// setting existed (an old cached page, or something hitting the API
        /// directly), which always got Czech - kept as-is. Anything else
        /// unrecognised gets English.
        /// </summary>
        public static string Pick(string lang, string cs, string en, string de = null, string es = null, string zh = null)
        {
            string code = (lang ?? "").Trim().ToLowerInvariant();
            int dash = code.IndexOf('-');
            if (dash >= 0) code = code.Substring(0, dash);

            switch (code)
            {
                case "": return cs;
                case "cs": return cs;
                case "de": return de ?? en;
                case "es": return es ?? en;
                case "zh": return zh ?? en;
                default: return en;
            }
        }
    }
}
