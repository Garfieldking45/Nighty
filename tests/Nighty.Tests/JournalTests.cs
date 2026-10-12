using Microsoft.Win32;
using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class JournalTests
{
    private static (ChangeTransactions Tx, MemoryJournalStore Store, DictResolver Res) Make()
    {
        var store = new MemoryJournalStore();
        var res = new DictResolver();
        var tx = new ChangeTransactions(store, res, new ManualTime());
        tx.Load();
        return (tx, store, res);
    }

    [Fact]
    public void The_original_value_is_saved_to_disk_before_the_setting_is_changed()
    {
        var (tx, store, res) = Make();
        var t = res.Add(new FakeTarget("a", "old"));
        JournalEntry? seen = null;
        t.BeforeWrite = _ => seen = store.Snapshot().Entries.SingleOrDefault();
        var r = tx.Apply("s1", "act", t, "new");
        Assert.Equal(ApplyStatus.Verified, r.Status);
        Assert.NotNull(seen);                         // the record already existed when the write happened
        Assert.Equal("old", seen!.Original);
        Assert.Equal(EntryState.Planned, seen.State);
        Assert.Equal("new", t.Value);
        Assert.Equal(EntryState.Applied, store.Snapshot().Entries.Single().State);
    }

    [Fact]
    public void Restore_puts_back_the_real_original_not_a_default()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "weird-original-7"));
        tx.Apply("s", "act", t, "x");
        var rep = tx.RestoreAll();
        Assert.Equal("weird-original-7", t.Value);
        Assert.Equal(1, rep.Restored);
        Assert.True(rep.FullyRestored);
    }

    [Fact]
    public void A_setting_that_did_not_exist_is_removed_again()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", null));
        tx.Apply("s", "act", t, "1");
        Assert.Equal("1", t.Value);
        tx.RestoreAll();
        Assert.Null(t.Value);
    }

    [Fact]
    public void A_value_changed_by_the_user_meanwhile_is_left_alone_and_reported()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old"));
        tx.Apply("s", "act", t, "nighty");
        t.Value = "user-choice";
        var rep = tx.RestoreAll();
        Assert.Equal("user-choice", t.Value);
        Assert.Equal(1, rep.SkippedChanged);
        Assert.Equal(0, rep.Restored);
        Assert.True(rep.FullyRestored);                          // nothing failed...
        Assert.Contains("left as they are", rep.Summary());      // ...but the report says what happened
    }

    [Fact]
    public void A_setting_that_cannot_be_read_is_not_touched()
    {
        var (tx, store, res) = Make();
        var t = res.Add(new FakeTarget("a", "old") { ReadAs = ReadStatus.Denied });
        var r = tx.Apply("s", "act", t, "new");
        Assert.Equal(ApplyStatus.CouldNotRead, r.Status);
        Assert.Equal(0, t.Writes);
        Assert.Empty(store.Snapshot().Entries);
    }

    [Fact]
    public void A_refused_change_leaves_nothing_to_undo()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old") { FailWrite = true });
        var r = tx.Apply("s", "act", t, "new");
        Assert.Equal(ApplyStatus.Failed, r.Status);
        Assert.False(tx.HasPending);
        Assert.Equal("old", t.Value);
    }

    [Fact]
    public void A_change_that_silently_did_not_take_is_reported_unverified_but_stays_restorable()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old") { IgnoreWrite = true });
        var r = tx.Apply("s", "act", t, "new");
        Assert.Equal(ApplyStatus.Unverified, r.Status);
        Assert.True(tx.HasPending);
        var rep = tx.RestoreAll();
        Assert.Equal(1, rep.Total);
        Assert.False(tx.HasPending);
    }

    [Fact]
    public void If_the_recovery_record_cannot_be_saved_nothing_is_changed()
    {
        var (tx, store, res) = Make();
        store.FailSave = true;
        var t = res.Add(new FakeTarget("a", "old"));
        var r = tx.Apply("s", "act", t, "new");
        Assert.Equal(ApplyStatus.JournalFailed, r.Status);
        Assert.Equal(0, t.Writes);
        Assert.Equal("old", t.Value);
    }

    [Fact]
    public void Setting_the_value_it_already_has_changes_nothing_and_records_nothing()
    {
        var (tx, store, res) = Make();
        var t = res.Add(new FakeTarget("a", "same"));
        var r = tx.Apply("s", "act", t, "same");
        Assert.Equal(ApplyStatus.AlreadyInPlace, r.Status);
        Assert.Equal(0, t.Writes);
        Assert.Empty(store.Snapshot().Entries);
    }

    [Fact]
    public void Restoring_twice_is_safe_and_does_not_touch_the_setting_again()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old"));
        tx.Apply("s", "act", t, "new");
        tx.RestoreAll();
        int writes = t.Writes;
        t.Value = "later-user-value";
        var again = tx.RestoreAll();
        Assert.Equal(writes, t.Writes);
        Assert.Equal("later-user-value", t.Value);
        Assert.Equal(0, again.Total);
    }

    [Fact]
    public void A_crash_between_saving_and_writing_is_handled_either_way()
    {
        // Record exists (Planned) but the write never happened: the value still equals the original.
        var store = new MemoryJournalStore();
        var res = new DictResolver();
        var t = res.Add(new FakeTarget("a", "old"));
        store.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "a", Original = "old", Intended = "new", State = EntryState.Planned } } }, out _);
        var tx = new ChangeTransactions(store, res); tx.Load();
        var rep = tx.RestoreAll();
        Assert.Equal("old", t.Value);
        Assert.Equal(0, t.Writes);
        Assert.Equal(1, rep.NothingToDo);

        // Record exists and the write did happen before the crash.
        var t2 = res.Add(new FakeTarget("b", "new"));
        store.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "b", Original = "old", Intended = "new", State = EntryState.Planned } } }, out _);
        var tx2 = new ChangeTransactions(store, res); tx2.Load();
        tx2.RestoreAll();
        Assert.Equal("old", t2.Value);
    }

    [Fact]
    public void A_new_run_restores_what_an_earlier_run_left_behind_after_a_restart()
    {
        var store = new MemoryJournalStore();
        var res = new DictResolver();
        var t = res.Add(new FakeTarget("power", "balanced"));
        var first = new ChangeTransactions(store, res); first.Load();
        first.Apply("s", "power", t, "high");
        // "Crash": the first instance is abandoned. A new process starts and loads the journal from disk.
        var second = new ChangeTransactions(store, res); second.Load();
        Assert.True(second.HasPending);
        var rep = second.RestoreAll();
        Assert.Equal("balanced", t.Value);
        Assert.True(rep.FullyRestored);
        Assert.False(second.HasPending);
    }

    [Fact]
    public void A_failed_restore_is_reported_and_retried_next_time()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old"));
        tx.Apply("s", "act", t, "new");
        t.FailWrite = true;
        var rep = tx.RestoreAll();
        Assert.Equal(1, rep.Failed);
        Assert.False(rep.FullyRestored);
        Assert.Contains("could NOT be restored", rep.Summary());
        Assert.True(tx.HasPending);
        t.FailWrite = false;
        var retry = tx.RestoreAll();
        Assert.True(retry.FullyRestored);
        Assert.Equal("old", t.Value);
    }

    [Fact]
    public void A_restore_that_does_not_read_back_as_the_original_is_not_called_restored()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("a", "old"));
        tx.Apply("s", "act", t, "new");
        t.IgnoreWrite = true;     // Windows accepts the write but nothing changes
        var rep = tx.RestoreAll();
        Assert.False(rep.FullyRestored);
        Assert.Equal(1, rep.Failed);
    }

    [Fact]
    public void An_entry_for_a_target_nighty_cannot_find_is_a_failure_not_a_success()
    {
        var store = new MemoryJournalStore();
        store.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "mystery", Original = "o", Intended = "n", State = EntryState.Applied } } }, out _);
        var tx = new ChangeTransactions(store, new DictResolver()); tx.Load();
        var rep = tx.RestoreAll();
        Assert.Equal(1, rep.Failed);
        Assert.False(rep.FullyRestored);
    }

    [Fact]
    public void A_target_that_no_longer_exists_is_obsolete_not_an_error()
    {
        var (tx, _, res) = Make();
        var t = res.Add(new FakeTarget("proc", "normal"));
        tx.Apply("s", "act", t, "high");
        t.ReadAs = ReadStatus.Gone;
        var rep = tx.RestoreAll();
        Assert.Equal(1, rep.Obsolete);
        Assert.True(rep.FullyRestored);
    }

    [Fact]
    public void Restoring_one_session_leaves_other_sessions_alone()
    {
        var (tx, _, res) = Make();
        var a = res.Add(new FakeTarget("a", "a0")); var b = res.Add(new FakeTarget("b", "b0"));
        tx.Apply("s1", "x", a, "a1"); tx.Apply("s2", "x", b, "b1");
        tx.RestoreAll("s1");
        Assert.Equal("a0", a.Value);
        Assert.Equal("b1", b.Value);
        Assert.True(tx.HasPending);
    }

    [Fact]
    public void Old_finished_entries_are_pruned_but_unfinished_ones_never_are()
    {
        var (tx, store, res) = Make();
        for (int i = 0; i < 60; i++) { var t = res.Add(new FakeTarget("k" + i, "o")); tx.Apply("s", "x", t, "n"); }
        var open = res.Add(new FakeTarget("open", "o")); tx.Apply("s2", "x", open, "n");
        tx.RestoreAll("s");
        var left = store.Snapshot().Entries;
        Assert.True(left.Count <= 41);
        Assert.Contains(left, e => e.TargetKey == "open" && e.State == EntryState.Applied);
    }
}

public class JournalFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nighty-journal-" + Guid.NewGuid().ToString("N"));
    public JournalFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_journal_round_trips_through_the_file()
    {
        var s = new FileJournalStore(Path.Combine(_dir, "j.json"));
        Assert.Equal(LoadState.Missing, s.Load().State);
        Assert.True(s.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "k", Original = null, Intended = "1", State = EntryState.Applied } } }, out _));
        var l = s.Load();
        Assert.Equal(LoadState.Ok, l.State);
        Assert.Null(l.File.Entries.Single().Original);
        Assert.Equal(EntryState.Applied, l.File.Entries.Single().State);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_previous_copy()
    {
        var path = Path.Combine(_dir, "j.json");
        var s = new FileJournalStore(path);
        s.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "one", State = EntryState.Applied } } }, out _);
        s.Save(new JournalFile { Entries = { new JournalEntry { TargetKey = "two", State = EntryState.Applied } } }, out _);
        File.WriteAllText(path, "{ not json");
        var l = s.Load();
        Assert.Equal(LoadState.RecoveredFromBackup, l.State);
        Assert.Equal("one", l.File.Entries.Single().TargetKey);
    }

    [Fact]
    public void A_journal_that_cannot_be_read_at_all_is_reported_as_unreadable()
    {
        var path = Path.Combine(_dir, "j.json");
        File.WriteAllText(path, "garbage"); File.WriteAllText(path + ".bak", "also garbage");
        var l = new FileJournalStore(path).Load();
        Assert.Equal(LoadState.Unreadable, l.State);
        Assert.Empty(l.File.Entries);
    }
}

/// <summary>The real registry target against a scratch key (never a key Nighty or Windows uses).</summary>
public class RegistryTargetTests : IDisposable
{
    private readonly string _key = @"Software\NightyTests\" + Guid.NewGuid().ToString("N");
    public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(_key, false); } catch { } }

    [Fact]
    public void Registry_values_keep_their_type_and_absence_through_apply_and_restore()
    {
        var res = new DefaultTargetResolver(new NativeProcessControl());
        var tx = new ChangeTransactions(new MemoryJournalStore(), res); tx.Load();

        using (var k = Registry.CurrentUser.CreateSubKey(_key)) { k.SetValue("D", 7, RegistryValueKind.DWord); k.SetValue("S", "hello", RegistryValueKind.String); }
        var d = new RegistryValueTarget(_key, "D"); var s = new RegistryValueTarget(_key, "S"); var gone = new RegistryValueTarget(_key, "Missing");

        Assert.Equal(ApplyStatus.Verified, tx.Apply("x", "t", d, RegistryValueTarget.Dword(1)).Status);
        Assert.Equal(ApplyStatus.Verified, tx.Apply("x", "t", s, RegistryValueTarget.Str("changed")).Status);
        Assert.Equal(ApplyStatus.Verified, tx.Apply("x", "t", gone, RegistryValueTarget.Dword(5)).Status);

        var rep = tx.RestoreAll();
        Assert.True(rep.FullyRestored);
        using var r = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(7, r.GetValue("D"));
        Assert.Equal(RegistryValueKind.DWord, r.GetValueKind("D"));
        Assert.Equal("hello", r.GetValue("S"));
        Assert.Null(r.GetValue("Missing"));
    }

    [Fact]
    public void A_registry_value_the_user_changed_is_not_overwritten_on_restore()
    {
        var tx = new ChangeTransactions(new MemoryJournalStore(), new DefaultTargetResolver(new NativeProcessControl())); tx.Load();
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("D", 1, RegistryValueKind.DWord);
        var d = new RegistryValueTarget(_key, "D");
        tx.Apply("x", "t", d, RegistryValueTarget.Dword(0));
        using (var k = Registry.CurrentUser.OpenSubKey(_key, true)!) k.SetValue("D", 42, RegistryValueKind.DWord);
        var rep = tx.RestoreAll();
        Assert.Equal(1, rep.SkippedChanged);
        using var r = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(42, r.GetValue("D"));
    }
}
