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

        // Safety net: the model sometimes writes a stage direction as plain prose with
        // NO asterisks, in the third person - "His head bobs up and down like a
        // bobblehead doll." Left alone that gets spoken out loud. Pull those sentences
        // out into actions too.
        var speech = ExtractProseStageDirections(Tidy(spoken.ToString()), actions);

        return (speech, [.. actions]);
    }

    private static readonly string[] BodyParts =
        ["head", "antenna", "antennas", "antennae", "torso", "body", "eye", "eyes",
         "optic", "optics", "visor", "frame", "chassis", "display", "screen", "face",
         "chest", "shoulder", "shoulders"];

    // Stems, matched as the start of a word, so "bob" catches bobs/bobbed/bobbing.
    private static readonly string[] MotionVerbs =
        ["bob", "nod", "tilt", "rotat", "twitch", "sway", "perk", "droop", "spin",
         "wiggl", "swivel", "bounc", "wobbl", "trembl", "quiver", "whir", "blink",
         "flash", "shak", "slump", "jerk"];

    /// <summary>
    /// Moves any sentence that is really an un-asterisked stage direction from the spoken
    /// text into <paramref name="actions"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. A sentence is only treated as a stage direction when it
    /// describes a body part MOVING and contains no first- or second-person words - because
    /// an actual line to Aubs almost always says "I", "my", or "you". So "His head bobs up
    /// and down" is pulled out, while "I nod", "your antennas are cool", and "Uzi built a
    /// railgun" are all left as speech.
    /// </remarks>
    private static string ExtractProseStageDirections(string spoken, List<string> actions)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return spoken;

        var sentences = System.Text.RegularExpressions.Regex.Split(spoken, @"(?<=[.!?])\s+");
        var kept = new List<string>();

        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();
            if (s.Length == 0) continue;
            if (IsProseStageDirection(s)) actions.Add(s);
            else kept.Add(s);
        }

        return string.Join(" ", kept).Trim();
    }

    private static bool IsProseStageDirection(string sentence)
    {
        var l = sentence.ToLowerInvariant();

        // A real line to Aubs almost always uses first or second person; never strip those.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                l, @"\b(i|i'm|i've|i'll|i'd|my|me|mine|myself|we|us|our|let's|you|your|yours|you're|we're)\b"))
            return false;

        var hasBody = BodyParts.Any(b =>
            System.Text.RegularExpressions.Regex.IsMatch(l, $@"\b{b}\b"));
        var hasMotion = MotionVerbs.Any(v =>
            System.Text.RegularExpressions.Regex.IsMatch(l, $@"\b{v}\w*"));

        return hasBody && hasMotion;
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
