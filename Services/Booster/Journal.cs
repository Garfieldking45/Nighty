using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

public enum EntryState
{
    /// <summary>Saved before the change was made. If Nighty stops here the change may or may not have happened; restore checks.</summary>
    Planned,
    Applied,
    /// <summary>The change was refused and the setting still has its original value. Nothing to undo.</summary>
    ApplyFailed,
    /// <summary>The original value is back and was read back to confirm it.</summary>
    Restored,
    /// <summary>Someone else changed the setting after Nighty did, so Nighty left it alone.</summary>
    RestoreSkippedChanged,
    /// <summary>Nighty tried and could not put the original back. It will try again.</summary>
    RestoreFailed,
    /// <summary>The thing no longer exists (a process that exited) or does not outlive a restart. Nothing left to restore.</summary>
    Obsolete,
}

public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The value read from the system before the change. Null means the setting did not exist.</summary>
    public string? Original { get; set; }
    public string? Intended { get; set; }
    public EntryState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? Note { get; set; }

    [JsonIgnore]
    public bool NeedsRestore => State is EntryState.Planned or EntryState.Applied or EntryState.RestoreFailed;
}

public sealed class JournalFile
{
    public int Version { get; set; } = 1;
    public List<JournalEntry> Entries { get; set; } = new();
}

public enum LoadState { Missing, Ok, RecoveredFromBackup, Unreadable }

public sealed record JournalLoad(JournalFile File, LoadState State, string? Detail = null);

public interface IJournalStore
{
    JournalLoad Load();
    /// <summary>Writes the journal durably. Returns false when it could not be saved.</summary>
    bool Save(JournalFile file, out string? error);
}

/// <summary>Stores the journal as a JSON file, replacing it atomically and keeping the previous copy as a backup.</summary>
public sealed class FileJournalStore : IJournalStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly object _gate = new();

    public FileJournalStore(string path) => _path = path;

    public JournalLoad Load()
    {
        lock (_gate)
        {
            string bak = _path + ".bak";
            if (!File.Exists(_path) && !File.Exists(bak)) return new JournalLoad(new JournalFile(), LoadState.Missing);
            string? why = null;
            if (File.Exists(_path))
            {
                if (TryRead(_path, out var main, out why)) return new JournalLoad(main!, LoadState.Ok);
            }
            if (File.Exists(bak) && TryRead(bak, out var backup, out var why2))
                return new JournalLoad(backup!, LoadState.RecoveredFromBackup, "The main recovery file was unreadable (" + why + "); used the previous copy");
            return new JournalLoad(new JournalFile(), LoadState.Unreadable, why ?? "unreadable");
        }
    }

    private static bool TryRead(string path, out JournalFile? file, out string? error)
    {
        file = null; error = null;
        try
        {
            file = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(path), Json);
            if (file == null) { error = "empty"; return false; }
            file.Entries ??= new();
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool Save(JournalFile file, out string? error)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, file, Json);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, _path);
                error = null;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}

/// <summary>What happened to one change while applying it.</summary>
public enum ApplyStatus
{
    /// <summary>Changed, and reading it back confirmed the new value.</summary>
    Verified,
    /// <summary>Changed, but reading it back did not show the value Nighty set. The original is still saved for restoring.</summary>
    Unverified,
    /// <summary>Already had the wanted value, so nothing was changed and nothing needs restoring.</summary>
    AlreadyInPlace,
    /// <summary>Nighty could not read the current value, so it did not touch the setting (it could not promise to put it back).</summary>
    CouldNotRead,
    /// <summary>The recovery file could not be written, so the change was not made.</summary>
    JournalFailed,
    /// <summary>The system refused the change.</summary>
    Failed,
    /// <summary>The target does not exist any more.</summary>
    Gone,
}

public sealed record ApplyOutcome(ApplyStatus Status, string Detail, JournalEntry? Entry = null)
{
    public bool Changed => Status is ApplyStatus.Verified or ApplyStatus.Unverified;
    public bool Ok => Status is ApplyStatus.Verified or ApplyStatus.AlreadyInPlace;
}

public sealed record RestoreReport(int Restored, int SkippedChanged, int Failed, int Obsolete, int NothingToDo, IReadOnlyList<string> Notes)
{
    /// <summary>True only when every change was confirmed back to its original value (or no longer exists). A setting left alone because the user changed it is reported separately.</summary>
    public bool FullyRestored => Failed == 0;
    public int Total => Restored + SkippedChanged + Failed + Obsolete + NothingToDo;

    public static readonly RestoreReport Empty = new(0, 0, 0, 0, 0, Array.Empty<string>());

    public string Summary()
    {
        if (Total == 0) return "Nothing needed restoring.";
        var parts = new List<string>();
        if (Restored > 0) parts.Add($"{Restored} restored and confirmed");
        if (Obsolete > 0) parts.Add($"{Obsolete} no longer exist");
        if (NothingToDo > 0) parts.Add($"{NothingToDo} were already back to normal");
        if (SkippedChanged > 0) parts.Add($"{SkippedChanged} left as they are because they were changed after Nighty's change");
        if (Failed > 0) parts.Add($"{Failed} could NOT be restored (Nighty will retry)");
        return string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// Applies and undoes single changes with a durable record: the original value is read and saved to disk before anything is
/// changed, the result is read back, and an undo only touches a setting that still holds the value Nighty set.
/// </summary>
public sealed class ChangeTransactions
{
    private readonly IJournalStore _store;
    private readonly ITargetResolver _resolver;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private JournalFile _file = new();

    public ChangeTransactions(IJournalStore store, ITargetResolver resolver, TimeProvider? time = null, Action<string>? log = null)
    {
        _store = store; _resolver = resolver; _time = time ?? TimeProvider.System; _log = log ?? (_ => { });
    }

    /// <summary>How the saved recovery file was read at startup.</summary>
    public JournalLoad LoadResult { get; private set; } = new(new JournalFile(), LoadState.Missing);

    public void Load()
    {
        lock (_gate)
        {
            LoadResult = _store.Load();
            _file = LoadResult.File;
            if (LoadResult.State == LoadState.Unreadable) _log("Recovery file unreadable: " + LoadResult.Detail);
        }
    }

    public IReadOnlyList<JournalEntry> Entries { get { lock (_gate) return _file.Entries.ToList(); } }
    public IReadOnlyList<JournalEntry> Pending { get { lock (_gate) return _file.Entries.Where(e => e.NeedsRestore).ToList(); } }
    public bool HasPending { get { lock (_gate) return _file.Entries.Any(e => e.NeedsRestore); } }

    private bool Persist(out string? error) => _store.Save(_file, out error);

    public ApplyOutcome Apply(string sessionId, string actionId, ISettingTarget target, string? intended)
    {
        lock (_gate)
        {
            var before = target.Read();
            if (before.Status == ReadStatus.Gone) return new ApplyOutcome(ApplyStatus.Gone, before.Detail ?? "No longer there");
            if (!before.IsOk) return new ApplyOutcome(ApplyStatus.CouldNotRead, before.Detail ?? "Could not read the current value");
            if (before.Value == intended) return new ApplyOutcome(ApplyStatus.AlreadyInPlace, "Already set");

            var entry = new JournalEntry
            {
                SessionId = sessionId, ActionId = actionId, TargetKey = target.Key, Description = target.Description,
                Original = before.Value, Intended = intended, State = EntryState.Planned, CreatedUtc = _time.GetUtcNow().UtcDateTime,
            };
            _file.Entries.Add(entry);
            if (!Persist(out var saveError))
            {
                _file.Entries.Remove(entry);
                return new ApplyOutcome(ApplyStatus.JournalFailed, "Could not save the recovery record (" + saveError + "), so nothing was changed");
            }

            var write = target.Write(intended);
            var after = target.Read();
            if (!write.Ok)
            {
                if (after.IsOk && after.Value == before.Value)
                {
                    entry.State = EntryState.ApplyFailed; entry.FinishedUtc = _time.GetUtcNow().UtcDateTime; entry.Note = write.Detail;
                    Persist(out _);
                    return new ApplyOutcome(ApplyStatus.Failed, write.Detail ?? "The change was refused", entry);
                }
                // The write reported failure but the value moved: keep the record so it can still be put back.
                entry.State = EntryState.Applied; entry.Note = "Write reported an error but the value changed: " + write.Detail;
                Persist(out _);
                return new ApplyOutcome(ApplyStatus.Failed, write.Detail ?? "The change reported an error", entry);
            }

            entry.State = EntryState.Applied;
            if (after.IsOk && after.Value == intended)
            {
                Persist(out _);
                return new ApplyOutcome(ApplyStatus.Verified, "Applied and confirmed", entry);
            }
            entry.Note = "Read back " + (after.IsOk ? $"'{after.Value}'" : "nothing") + " instead of '" + intended + "'";
            Persist(out _);
            return new ApplyOutcome(ApplyStatus.Unverified, "Applied, but could not confirm the new value", entry);
        }
    }

    /// <summary>Puts one change back. Safe to call again: finished entries are not touched twice.</summary>
    public EntryState Restore(JournalEntry entry, List<string>? notes = null)
    {
        lock (_gate)
        {
            if (!entry.NeedsRestore) return entry.State;
            var target = _resolver.Resolve(entry.TargetKey);
            if (target == null)
            {
                Finish(entry, EntryState.RestoreFailed, "Nighty does not know how to reach " + entry.TargetKey);
                notes?.Add($"{entry.Description}: unknown target");
                return entry.State;
            }

            var now = target.Read();
            if (now.Status == ReadStatus.Gone) { Finish(entry, EntryState.Obsolete, now.Detail ?? "No longer exists"); return entry.State; }
            if (!now.IsOk) { Finish(entry, EntryState.RestoreFailed, "Could not read it: " + now.Detail); notes?.Add($"{entry.Description}: {now.Detail}"); return entry.State; }

            if (now.Value == entry.Original) { Finish(entry, EntryState.Restored, "Already had its original value"); return entry.State; }
            if (now.Value != entry.Intended)
            {
                // Planned entries that never reached the write also end up here when something else changed the value.
                Finish(entry, EntryState.RestoreSkippedChanged, $"Left alone: it now holds '{now.Value}', not the '{entry.Intended}' Nighty set");
                notes?.Add($"{entry.Description}: changed by you or another app, left as is");
                return entry.State;
            }

            var write = target.Write(entry.Original);
            var verify = target.Read();
            if (write.Ok && verify.IsOk && verify.Value == entry.Original) Finish(entry, EntryState.Restored, null);
            else
            {
                Finish(entry, EntryState.RestoreFailed, write.Ok ? "Wrote the original back but could not confirm it" : write.Detail);
                notes?.Add($"{entry.Description}: {entry.Note}");
            }
            return entry.State;
        }
    }

    private void Finish(JournalEntry e, EntryState state, string? note)
    {
        e.State = state;
        e.Note = note ?? e.Note;
        if (state != EntryState.RestoreFailed) e.FinishedUtc = _time.GetUtcNow().UtcDateTime;
        Persist(out _);
    }

    /// <summary>Restores every unfinished change (optionally just one session's), newest first, and reports honestly what happened.</summary>
    public RestoreReport RestoreAll(string? sessionId = null, Func<JournalEntry, bool>? filter = null)
    {
        List<JournalEntry> todo;
        lock (_gate) todo = _file.Entries.Where(e => e.NeedsRestore && (sessionId == null || e.SessionId == sessionId) && (filter == null || filter(e))).Reverse().ToList();
        var notes = new List<string>();
        foreach (var e in todo) Restore(e, notes);
        var report = Summarize(todo, notes);
        Prune();
        return report;
    }

    private static RestoreReport Summarize(List<JournalEntry> entries, List<string> notes)
    {
        int restored = 0, skipped = 0, failed = 0, obsolete = 0, nothing = 0;
        foreach (var e in entries)
        {
            switch (e.State)
            {
                case EntryState.Restored when e.Note == "Already had its original value": nothing++; break;
                case EntryState.Restored: restored++; break;
                case EntryState.RestoreSkippedChanged: skipped++; break;
                case EntryState.Obsolete: obsolete++; break;
                default: failed++; break;
            }
        }
        return new RestoreReport(restored, skipped, failed, obsolete, nothing, notes);
    }

    /// <summary>Drops old finished entries, keeping a short history for diagnostics.</summary>
    public void Prune(int keepFinished = 40)
    {
        lock (_gate)
        {
            var finished = _file.Entries.Where(e => !e.NeedsRestore).OrderBy(e => e.FinishedUtc ?? e.CreatedUtc).ToList();
            int drop = finished.Count - keepFinished;
            if (drop <= 0) return;
            foreach (var e in finished.Take(drop)) _file.Entries.Remove(e);
            Persist(out _);
        }
    }

    /// <summary>Records a change that cannot or need not be undone through a target (for example memory that was trimmed), so the report is complete.</summary>
    public void Note(string sessionId, string actionId, string description, string note, EntryState state = EntryState.Obsolete)
    {
        lock (_gate)
        {
            _file.Entries.Add(new JournalEntry
            {
                SessionId = sessionId, ActionId = actionId, TargetKey = "note", Description = description, State = state,
                CreatedUtc = _time.GetUtcNow().UtcDateTime, FinishedUtc = _time.GetUtcNow().UtcDateTime, Note = note,
            });
            Persist(out _);
        }
    }
}
