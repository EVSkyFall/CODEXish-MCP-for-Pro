using System.Text;
using System.Text.RegularExpressions;

namespace Codexish.Server;

// In-process literal and regex matching for fs_search and artifact_search. No external searcher is
// launched, so a search never inherits a shell grant.
public static class TextSearch
{
    public const int PreviewChars = 200;

    public static Func<string, List<(int Start, int Length)>> Compile(string query, bool regex)
    {
        if (!regex)
            return line =>
            {
                List<(int, int)> spans = [];
                int index = line.IndexOf(query, StringComparison.Ordinal);
                while (index >= 0)
                {
                    spans.Add((index, query.Length));
                    index = index + query.Length <= line.Length
                        ? line.IndexOf(query, index + Math.Max(1, query.Length), StringComparison.Ordinal) : -1;
                }
                return spans;
            };
        Regex compiled;
        try { compiled = new Regex(query, RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException e) { throw new CodexishFault("INVALID_ARGUMENT", $"Invalid regular expression: {e.Message}"); }
        return line =>
        {
            List<(int, int)> spans = [];
            try
            {
                foreach (Match match in compiled.Matches(line))
                    if (match.Length > 0) spans.Add((match.Index, match.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                throw new CodexishFault("EXECUTION_FAILED", "The regular expression took too long on one line; use a more specific pattern.");
            }
            return spans;
        };
    }

    // Streams UTF-8 lines with their byte offsets so search results and cursors refer to real file positions.
    public static IEnumerable<(string Text, long ByteStart, int ByteLength)> Lines(Stream stream)
    {
        var decoder = new UTF8Encoding(false);
        byte[] chunk = new byte[65536];
        var line = new List<byte>(256);
        long lineStart = 0, position = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte value = chunk[i];
                position++;
                if (value != (byte)'\n') { line.Add(value); continue; }
                int length = line.Count;
                if (length > 0 && line[length - 1] == (byte)'\r') length--;
                yield return (decoder.GetString(line.ToArray(), 0, length), lineStart, line.Count + 1);
                line.Clear();
                lineStart = position;
            }
        }
        if (line.Count > 0)
            yield return (decoder.GetString(line.ToArray()), lineStart, line.Count);
    }

    public static string Preview(string line, int index)
    {
        if (line.Length <= PreviewChars) return line;
        int start = Math.Max(0, index - PreviewChars / 2);
        int length = Math.Min(PreviewChars, line.Length - start);
        return (start > 0 ? "..." : "") + line.Substring(start, length) + (start + length < line.Length ? "..." : "");
    }
}
