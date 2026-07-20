using System.Text;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Separates what Rose SAYS from what Rose DOES.
/// </summary>
/// <remarks>
/// Roleplay models narrate physical action inline, in asterisks:
/// "*Antennas twitch excitedly* Wait, really?!". The personas actively encourage
/// this - they tell each character it has a head, antennas and a rotating torso and
/// to react physically before speaking - so this is the model doing as it was asked,
/// not misbehaving.
///
/// It still must never reach the synthesiser, which would read the punctuation out
/// loud. Splitting rather than deleting keeps the stage direction available to drive
/// the actual servos, which is the whole reason the personas ask for it.
/// </remarks>
public static class SpokenText
{
    /// <summary>
    /// Splits model output into the words to speak and the actions described.
    /// </summary>
    public static (string Spoken, string[] Actions) Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", []);

        var spoken = new StringBuilder();
        var actions = new List<string>();
        var current = new StringBuilder();
        var inAction = false;

        foreach (var ch in text)
        {
            // Both markdown conventions show up: *action* and _action_.
            if (ch is '*' or '_')
            {
                if (inAction)
                {
                    var a = current.ToString().Trim();
                    if (a.Length > 0) actions.Add(a);
                    current.Clear();
                    inAction = false;
                }
                else
                {
                    spoken.Append(current);
                    current.Clear();
                    inAction = true;
                }
                continue;
            }

            current.Append(ch);
        }

        // An unclosed marker means the action ran to the end of the text. Treat the
        // remainder as action rather than speech - a half-written stage direction is
        // still not something to say out loud.
        if (inAction)
        {
            var a = current.ToString().Trim();
            if (a.Length > 0) actions.Add(a);
        }
        else spoken.Append(current);

        return (Tidy(spoken.ToString()), [.. actions]);
    }

    /// <summary>
    /// Collapses the whitespace and orphaned punctuation left behind by removing
    /// an action from the middle of a sentence.
    /// </summary>
    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = true; // trims the leading edge as a side effect

        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }

            // A stripped leading action usually leaves ", text" or "- text".
            if (sb.Length == 0 && (ch == ',' || ch == '-' || ch == ':' || ch == ';')) continue;

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>True if there is nothing left worth sending to the synthesiser.</summary>
    public static bool IsSayable(string spoken) => spoken.Any(char.IsLetterOrDigit);
}
