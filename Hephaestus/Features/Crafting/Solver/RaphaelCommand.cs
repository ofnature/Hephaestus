using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>Everything raphael-cli needs to solve one craft. Two equal requests have the same solution.</summary>
/// <param name="RecipeLevel">RecipeLevelTable row id ("rlvl").</param>
/// <param name="InitialQuality">Quality already present at step 1, from HQ materials.</param>
public readonly record struct BaselineRequest(
    int RecipeLevel,
    int MaxProgress,
    int MaxQuality,
    int MaxDurability,
    bool Expert,
    int JobLevel,
    int BaseProgress,
    int BaseQuality,
    int Craftsmanship,
    int Control,
    int MaxCp,
    int InitialQuality,
    bool Manipulation,
    bool HeartAndSoul,
    bool QuickInnovation,
    bool BackloadProgress);

/// <summary>
/// The raphael-cli command line and its output, as pure text — so both are testable without
/// spawning anything. Invocation confirmed against the v0.28.6 binary we build (docs/artisan-notes.md).
/// </summary>
public static class RaphaelCommand
{
    /// <summary>
    /// Arguments for <c>raphael-cli solve</c>. We hand it the recipe as numbers
    /// (<c>--custom-recipe</c>) and the base increases we computed ourselves
    /// (<c>--override-base-increases</c>), so the CLI never needs game data matching the client's patch.
    /// </summary>
    public static string Arguments(in BaselineRequest request, int threads)
    {
        var args = new StringBuilder("solve");
        args.Append(Invariant($" --custom-recipe {request.RecipeLevel} {request.MaxProgress} {request.MaxQuality} {request.MaxDurability} {(request.Expert ? 1 : 0)}"));
        args.Append(Invariant($" --override-base-increases {request.JobLevel} {request.BaseProgress} {request.BaseQuality}"));
        args.Append(Invariant($" --stats {request.Craftsmanship} {request.Control} {request.MaxCp}"));
        args.Append(Invariant($" --level {request.JobLevel}"));
        if (request.InitialQuality > 0)
            args.Append(Invariant($" --initial-quality {request.InitialQuality}"));
        if (request.Manipulation)
            args.Append(" --manipulation");
        if (request.HeartAndSoul)
            args.Append(" --heart-and-soul");
        if (request.QuickInnovation)
            args.Append(" --quick-innovation");
        if (request.BackloadProgress)
            args.Append(" --backload-progress");
        if (threads > 0)
            args.Append(Invariant($" --threads {threads}"));
        args.Append(" --output-variables actions");
        return args.ToString();
    }

    /// <summary>Cache key: the request alone. Thread count changes how fast the answer comes, not the answer.</summary>
    public static string CacheKey(in BaselineRequest request) => Arguments(request, threads: 0);

    /// <summary>
    /// Parse the <c>actions</c> output variable: a line like <c>"[MuscleMemory, Manipulation, ...]"</c>.
    /// Returns null when no such line is present or when it names an action the simulator does not
    /// model — a rotation we cannot replay faithfully is worse than no baseline.
    /// </summary>
    public static IReadOnlyList<CraftAction>? ParseActions(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().Trim('"');
            if (line.Length < 2 || line[0] != '[' || line[^1] != ']')
                continue;

            var actions = new List<CraftAction>();
            foreach (var name in line[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!CraftActionNames.TryParse(name, out var action))
                    return null;
                actions.Add(action);
            }

            return actions;
        }

        return null;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
