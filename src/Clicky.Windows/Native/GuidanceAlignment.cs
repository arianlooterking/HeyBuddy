using Clicky.Core;

namespace Clicky.Windows.Native;

/// <summary>Aligns model-drawn guidance with the fresh accessibility map. This class can only return drawings.</summary>
internal static class GuidanceAlignment
{
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "my", "is", "where", "what", "which", "show", "me", "to", "click", "press", "open", "button", "control", "step",
        "کجا", "کجاست", "هست", "این", "من", "را", "رو", "نشانم", "نشونم", "بده", "کدام", "کدوم", "دکمه", "کلیک",
        "bu", "bir", "nerede", "göster", "hangi", "düğme", "buton", "tıkla", "bas", "aç"
    };

    public static IReadOnlyList<GuidanceCommand> Align(IReadOnlyList<GuidanceCommand> commands, DesktopObservation? observation, ScreenTurnKind intent, string ownerRequest)
    {
        if (observation is null || observation.Elements.Count == 0)
            return commands;

        var aligned = commands.Select(command => AlignCommand(command, observation.Elements)).ToList();
        if (aligned.Count == 0 && intent == ScreenTurnKind.Locate && BestElement(ownerRequest, observation.Elements) is { } target)
            aligned.Add(new("circle", target.X, target.Y, Label: target.Name, Step: 1));
        return aligned;
    }

    private static GuidanceCommand AlignCommand(GuidanceCommand command, IReadOnlyList<DesktopObservationElement> elements)
    {
        var target = BestElement(command.Label, elements);
        if (target is null)
            return command with
            {
                Step = Math.Max(1, command.Step)
            };
        return command.Kind switch
        {
            "rectangle" => command with
            {
                X = target.Left,
                Y = target.Top,
                X2 = Math.Clamp(target.Left + target.Width, 0, 1),
                Y2 = Math.Clamp(target.Top + target.Height, 0, 1),
                Step = Math.Max(1, command.Step)
            },
            "arrow" => command with { X2 = target.X, Y2 = target.Y, Step = Math.Max(1, command.Step) },
            _ => command with { X = target.X, Y = target.Y, Step = Math.Max(1, command.Step) }
        };
    }

    private static DesktopObservationElement? BestElement(string text, IReadOnlyList<DesktopObservationElement> elements)
    {
        var requestTokens = Tokens(text);
        if (requestTokens.Count == 0)
            return null;
        return elements
            .Select(element => new { Element = element, Score = Score(requestTokens, Tokens(element.Name)) })
            .Where(match => match.Score >= 0.75)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Element.Width * match.Element.Height)
            .Select(match => match.Element)
            .FirstOrDefault();
    }

    private static double Score(HashSet<string> requested, HashSet<string> candidate)
    {
        if (candidate.Count == 0)
            return 0;
        var overlap = candidate.Count(requested.Contains);
        return overlap / (double)Math.Min(requested.Count, candidate.Count);
    }

    private static HashSet<string> Tokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!GenericWords.Contains(token))
                tokens.Add(token);
        return tokens;
    }
}
