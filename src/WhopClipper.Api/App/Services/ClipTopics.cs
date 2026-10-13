using System.Text.RegularExpressions;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Whether a finished clip is a car clip, which is what the clip list's "Cars only" switch keeps on screen.
///
/// A mark set by hand on the clip wins. Without one, a car named in the title or caption decides: that is right for
/// most clips and costs nothing, and the mark is there for the ones it gets wrong, such as a clip that shows a car
/// its title never names.
/// </summary>
public static partial class ClipTopics
{
    public static bool IsCar(EditPlan? plan, string title, string caption)
    {
        var marked = plan is null ? null : JsonDefaults.Parse<EditPlanExtrasDto?>(plan.ExtraSettingsJson, null)?.Car;
        return marked ?? MentionsCar($"{title} {caption}");
    }

    public static bool MentionsCar(string text) => CarWords().IsMatch(text);

    // Whole words only: "car" must not match "care" or "card", nor "rolls" a "payrolls".
    [GeneratedRegex(@"\b(cars?|whips?|supercars?|hypercars?|lambo(rghini)?s?|ferraris?|bugattis?|rolls|royce|cullinan|" +
                    @"urus|mclarens?|porsches?|bentleys?|maybachs?|koenigseggs?|hellcats?|brabus|g63|g-?wagons?|svjs?|" +
                    @"trucks?|trx|dealerships?|driv(e|es|ing)|drove|gas station)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CarWords();
}
