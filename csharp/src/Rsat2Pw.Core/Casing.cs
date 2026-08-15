using System.Text;

namespace Rsat2Pw;

public static class Casing
{
    public static string ToPascalCase(string input)
    {
        var words = SplitWords(input);
        var builder = new StringBuilder();

        foreach (var word in words)
        {
            builder.Append(char.ToUpperInvariant(word[0]));
            for (var i = 1; i < word.Length; i++)
            {
                builder.Append(char.ToLowerInvariant(word[i]));
            }
        }

        return builder.ToString();
    }

    private static List<string> SplitWords(string input)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (!char.IsAsciiLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var prev = current[^1];

                if (!char.IsAsciiLetterUpper(prev) && char.IsAsciiLetterUpper(c))
                {
                    Flush();
                }
                else if (char.IsAsciiLetterUpper(prev)
                         && char.IsAsciiLetterUpper(c)
                         && i + 1 < input.Length
                         && char.IsAsciiLetterLower(input[i + 1]))
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return words;
    }
}
