using Kairo.Core.Models;

namespace Kairo.Core.Agent;

/// <summary>Raised when Kairo would repeat the same action endlessly.</summary>
public sealed class LoopDetectedException(string message) : Exception(message);

/// <summary>
/// Prevents endless repetition loops: identical actions (same kind, target and value) may only run a limited
/// number of times, and planning rounds without any successful action are counted.
/// </summary>
public sealed class LoopGuard
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly int _maxRepeats;
    private int _roundsWithoutProgress;

    public LoopGuard(int maxRepeats = 3, int maxRoundsWithoutProgress = 3)
    {
        _maxRepeats = maxRepeats;
        MaxRoundsWithoutProgress = maxRoundsWithoutProgress;
    }

    public int MaxRoundsWithoutProgress { get; }

    public void Register(AgentAction action, UiElement? element)
    {
        var key = string.Join('|', action.Kind, element?.Locator ?? action.TargetLabel ?? "", action.Path ?? "", action.Keys ?? "",
            action.Url ?? "", action.App ?? "", action.Option ?? "", action.Checked?.ToString() ?? "", (action.Value ?? "").GetHashCode(StringComparison.Ordinal));
        var count = _counts.TryGetValue(key, out var c) ? c + 1 : 1;
        _counts[key] = count;
        if (count > _maxRepeats)
        {
            throw new LoopDetectedException(
                $"Kairo hat „{(string.IsNullOrWhiteSpace(action.Description) ? action.Kind.ToWireName() : action.Description)}“ mehrfach wiederholt und die Aufgabe gestoppt, um eine Endlosschleife zu vermeiden.");
        }
    }

    public void RoundFinished(bool anySuccess)
    {
        _roundsWithoutProgress = anySuccess ? 0 : _roundsWithoutProgress + 1;
        if (_roundsWithoutProgress >= MaxRoundsWithoutProgress)
        {
            throw new LoopDetectedException("Kairo kommt nicht weiter: mehrere Versuche hintereinander blieben ohne Fortschritt.");
        }
    }
}
