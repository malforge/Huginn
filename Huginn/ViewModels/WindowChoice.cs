namespace Huginn.ViewModels;

/// <summary>
/// A preset for how far back the service rules look. Presets rather than a number of minutes,
/// because the useful values are a handful of familiar spans and nobody thinks in minutes.
/// </summary>
/// <param name="Label">How a person would say it.</param>
/// <param name="Minutes">What the query actually uses.</param>
public sealed record WindowChoice(string Label, int Minutes)
{
    public override string ToString() => Label;
}
