using Kairo.Core.Models;

namespace Kairo.Core.AI.Planning;

public enum PlanContinuation
{
    /// <summary>After these steps the task should be complete – verify and finish.</summary>
    VerifyAndFinish,
    /// <summary>The screen will change or more information is needed – plan again with the new state.</summary>
    Replan,
    /// <summary>Information is missing – ask the user.</summary>
    AskUser,
}

public sealed record PlanFact(string Key, string Value);

/// <summary>Parsed planner output.</summary>
public sealed record Plan
{
    public string Status { get; init; } = "";
    public IReadOnlyList<AgentAction> Steps { get; init; } = [];
    public PlanContinuation After { get; init; } = PlanContinuation.VerifyAndFinish;
    public string? FinalMessage { get; init; }
    public string? Question { get; init; }
    public IReadOnlyList<PlanFact> Facts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Compact JSON of the plan (kept in the planner conversation).</summary>
    public string RawJson { get; init; } = "{}";
    public UsageInfo Usage { get; init; } = UsageInfo.Zero;
    public TimeSpan Latency { get; init; }
}

/// <summary>Outcome of one executed step, reported back to the planner.</summary>
public sealed record StepOutcome(AgentAction Action, bool Success, string Message, bool StructureChanged, string? Data = null);

/// <summary>Everything the planner needs for one round.</summary>
public sealed record PlanningInput
{
    public required string Instruction { get; init; }
    /// <summary>Compact UI state (already wrapped as untrusted data).</summary>
    public string? UiState { get; init; }
    public string? ActiveWindowDescription { get; init; }
    public IReadOnlyList<string> OtherWindows { get; init; } = [];
    /// <summary>File contents / search results (already wrapped as untrusted data).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
    public IReadOnlyList<StepOutcome> Outcomes { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Screenshot for vision rounds (PNG/JPEG bytes).</summary>
    public CapturedImageData? Screenshot { get; init; }
}

public sealed record CapturedImageData(byte[] Data, string MimeType, int Width, int Height);
