using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

public enum Confidence { Low, Medium, High }
public enum RiskLevel { Low, Medium, High }

/// <summary>What a piece of evidence is: something measured, an inference from measurements, or a gap in what Nighty can see.</summary>
public enum EvidenceKind { Observation, Hypothesis, Limitation }

public sealed record EvidenceLine(EvidenceKind Kind, string Text)
{
    public static EvidenceLine Observed(string text) => new(EvidenceKind.Observation, text);
    public static EvidenceLine Inferred(string text) => new(EvidenceKind.Hypothesis, text);
    public static EvidenceLine Missing(string text) => new(EvidenceKind.Limitation, text);
}

/// <summary>
/// A process is identified by its id and its creation time, never by its id alone: Windows reuses process ids, so an id on its
/// own can end up pointing at a different program than the one Nighty changed.
/// </summary>
public readonly record struct ProcIdentity(int Pid, long StartFileTime)
{
    public override string ToString() => $"{Pid}@{StartFileTime}";

    public static bool TryParse(string? text, out ProcIdentity id)
    {
        id = default;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('@');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long start)) return false;
        id = new ProcIdentity(pid, start);
        return true;
    }
}

/// <summary>Cheap listing entry: no handles opened.</summary>
public sealed record ProcEntry(int Pid, string Name, int ParentPid);

/// <summary>Details looked up on demand for one process. Null path means Windows refused to tell us.</summary>
public sealed record ProcDetails(ProcIdentity Id, string? Path);

public interface IProcessSource
{
    IReadOnlyList<ProcEntry> List();
    ProcDetails? Describe(int pid);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LauncherKind { Unknown, Official, Bloxstrap, Fishstrap, Voidstrap, Froststrap }
