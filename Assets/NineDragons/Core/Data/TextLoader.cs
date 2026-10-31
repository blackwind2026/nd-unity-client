using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NineDragons.Data
{
    /// <summary>
    /// Helpers for the CP949/Korean text data files. The data tokens we parse are all ASCII
    /// (numbers and lowercase effect names); the surrounding comments contain Korean bytes.
    /// We decode each byte 1:1 to a char (Latin1), which round-trips every ASCII token and
    /// never throws on high bytes — matching how the C++ fgets/sscanf loaders ignore the
    /// comment bytes entirely.
    /// </summary>
    internal static class TextLoader
    {
        /// <summary>Read a file as Latin1 lines (byte value == char value).</summary>
        public static List<string> ReadLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var lines = new List<string>();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b == (byte)'\n')
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else if (b == (byte)'\r')
                {
                    // swallow; handled by following \n (or end of file)
                }
                else
                {
                    sb.Append((char)b);
                }
            }
            if (sb.Length > 0) lines.Add(sb.ToString());
            return lines;
        }

        /// <summary>Split a line into whitespace-delimited tokens (drops empty tokens from runs of tabs/spaces).</summary>
        public static string[] Tokens(string line) =>
            line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);

        public static bool TryInt(string s, out int v) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        public static bool TryDouble(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
