using System;
using System.Globalization;

namespace Hephaestus.Features.Crafting.Driving;

/// <summary>
/// One synthesis as it went, told once it is over (<see cref="CraftSession.SynthesisEnded"/>): what
/// the crafting window showed last, and the time between its first sight and its last.
/// </summary>
/// <param name="Seconds">From the first tick the Synthesis window was seen to the tick it was gone.</param>
/// <param name="LastStep">The window's step counter when last seen.</param>
/// <param name="Expert">The expert solver made it (false = a planned rotation with the live evaluator, or nobody: solver off).</param>
/// <param name="TopRating">A rated collectable (Cosmic mission item): whether it stood at its top rating; null for anything else.</param>
/// <param name="FirstEstimate">The first "about N s left" the solver gave for it; negative = it never gave one.</param>
public readonly record struct SynthesisRecord(
    uint RecipeId,
    DateTime Started,
    double Seconds,
    int LastStep,
    int Progress,
    int MaxProgress,
    int Quality,
    int MaxQuality,
    bool Expert,
    bool? TopRating,
    double FirstEstimate)
{
    /// <summary>The progress bar was full when the window was last seen.</summary>
    public bool Finished => MaxProgress > 0 && Progress >= MaxProgress;

    /// <summary>One line for the chat window and the craft-times file.</summary>
    public string Describe(string item)
    {
        var culture = CultureInfo.InvariantCulture;
        if (!Finished)
            return string.Create(culture, $"{item}: NOT finished after {Seconds:F0} s — progress {Progress:N0} / {MaxProgress:N0} at step {LastStep}");

        var rating = TopRating switch
        {
            true => ", top rating",
            false => ", below the top rating",
            null => string.Empty,
        };
        var estimate = FirstEstimate >= 0 ? string.Create(culture, $", first estimate {FirstEstimate:F0} s") : string.Empty;
        var percent = MaxQuality > 0 ? (int)((long)Quality * 100 / MaxQuality) : 100;
        return string.Create(culture,
            $"{item}: {Seconds:F0} s, step {LastStep}, quality {Quality:N0} / {MaxQuality:N0} ({percent}%{rating}){(Expert ? ", expert solver" : string.Empty)}{estimate}");
    }
}
