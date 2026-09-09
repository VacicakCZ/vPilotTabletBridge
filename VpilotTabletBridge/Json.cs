using System.Text;

namespace VpilotTabletBridge
{
    /// <summary>Tiny hand-rolled JSON helpers, shared by MessageStore and PluginState.</summary>
    internal static class Json
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Str(string s)
        {
            return s == null ? "null" : "\"" + Escape(s) + "\"";
        }

        public static string Bool(bool b)
        {
            return b ? "true" : "false";
        }
    }
}
