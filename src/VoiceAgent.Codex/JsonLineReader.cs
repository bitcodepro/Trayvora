using System.Runtime.CompilerServices;
using System.Text;

namespace VoiceAgent.Codex;

internal static class JsonLineReader
{
    private const int MaximumLineLength = 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunk = new char[8192];
        var line = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length != 0)
                {
                    throw new InvalidDataException("Codex App Server closed output in the middle of a JSON line.");
                }

                yield break;
            }

            var start = 0;
            while (start < count)
            {
                var newline = Array.IndexOf(chunk, '\n', start, count - start);
                var end = newline < 0 ? count : newline;
                var length = end - start;
                if (line.Length + length > MaximumLineLength)
                {
                    throw new InvalidDataException("Codex App Server emitted an oversized JSON line.");
                }

                line.Append(chunk, start, length);
                if (newline >= 0)
                {
                    var result = line.ToString();
                    line.Clear();
                    yield return result.TrimEnd('\r');
                }

                start = end + 1;
            }
        }
    }
}
