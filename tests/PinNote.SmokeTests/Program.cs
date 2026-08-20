using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Core.Storage;

var tests = new (string Name, Func<Task> Run)[]
{
    ("planner selects due and next reminder", TestPlanner),
    ("reminder transitions preserve overdue state", TestTransitions),
    ("snapshot clone is independent", TestClone),
    ("schema normalization preserves groups and note state", TestSchema),
    ("json store round-trips and creates backup", TestStore)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL  {test.Name}: {exception.Message}");
        Console.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static Task TestPlanner()
{
    var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(8));
    var due = NewNote(now.AddMinutes(-1), ReminderState.Scheduled);
    var alreadyHandled = NewNote(now.AddMinutes(-2), ReminderState.Dismissed);
    var next = NewNote(now.AddMinutes(5), ReminderState.Scheduled);

    var notes = new[] { next, alreadyHandled, due };
    Assert(ReminderPlanner.GetDue(notes, now).SequenceEqual(new[] { due }), "Only the scheduled overdue note should be due.");
    Assert(ReminderPlanner.GetNextDue(notes, now) == next.ReminderAt, "The nearest future reminder should be selected.");
    return Task.CompletedTask;
}

static Task TestTransitions()
{
    var now = DateTimeOffset.Now;
    var weak = NewNote(now.AddMinutes(-1), ReminderState.Scheduled, ReminderLevel.Weak);
    ReminderStateMachine.Trigger(weak, now);
    Assert(weak.ReminderState == ReminderState.Dismissed, "Weak reminders should not re-open after restart.");
    Assert(weak.IsOverdue(now), "A handled reminder remains overdue until completed or rescheduled.");

    var ultra = NewNote(now.AddMinutes(-1), ReminderState.Scheduled, ReminderLevel.Ultra);
    ReminderStateMachine.Trigger(ultra, now);
    Assert(ultra.ReminderState == ReminderState.Triggered, "Ultra reminders should remain pending until user action.");
    ReminderStateMachine.Snooze(ultra, now.AddMinutes(5));
    Assert(ultra.ReminderState == ReminderState.Scheduled && !ultra.IsOverdue(now), "Snooze should return to scheduled state.");
    ReminderStateMachine.Complete(ultra);
    Assert(ultra.ReminderAt is null, "Complete should clear the reminder.");
    return Task.CompletedTask;
}

static Task TestClone()
{
    var snapshot = new NoteSnapshot { Notes = [new NoteDocument { Title = "Original" }] };
    snapshot.Groups.Add(new NoteGroup { Name = "Work" });
    snapshot.Notes[0].GroupId = snapshot.Groups[0].Id;
    snapshot.Notes[0].IsHidden = true;
    var clone = snapshot.Clone();
    clone.Notes[0].Title = "Changed";
    clone.Settings.EnableMaterial = false;
    clone.Settings.NewNoteHotkey = "Ctrl+Alt+N";
    clone.Settings.ManagerHotkeyEnabled = false;
    Assert(snapshot.Notes[0].Title == "Original", "Cloned notes must be independent.");
    Assert(snapshot.Settings.EnableMaterial, "Cloned settings must be independent.");
    Assert(snapshot.Settings.NewNoteHotkey == "Ctrl+Shift+N" && snapshot.Settings.ManagerHotkeyEnabled,
        "Cloned shortcut settings must be independent.");
    clone.Groups[0].Name = "Changed group";
    Assert(snapshot.Groups[0].Name == "Work", "Cloned groups must be independent.");
    Assert(clone.Notes[0].IsHidden && clone.Notes[0].GroupId == clone.Groups[0].Id, "Clone must preserve note management state.");
    return Task.CompletedTask;
}

static Task TestSchema()
{
    var known = new NoteGroup { Name = "  工作  " };
    var orphan = new NoteDocument
    {
        Title = "  测试  ",
        GroupId = Guid.NewGuid(),
        IsHidden = true,
        Left = 321.5,
        Top = 176.25,
        Width = 430,
        Height = 510,
        ModifiedAt = default
    };
    var snapshot = new NoteSnapshot { SchemaVersion = 1, Groups = [known], Notes = [orphan] };
    snapshot.Settings.NewNoteHotkey = "  ";
    snapshot.Settings.ManagerHotkey = "";
    snapshot.Settings.NewNoteHotkeyEnabled = false;
    snapshot.Normalize();

    Assert(snapshot.SchemaVersion == 2, "Old snapshots should normalize to the current schema.");
    Assert(known.Name == "工作", "Group names should normalize.");
    Assert(orphan.GroupId is null, "Unknown group references should move to ungrouped.");
    Assert(orphan.IsHidden, "Hidden state must survive normalization.");
    Assert(orphan.Left == 321.5 && orphan.Top == 176.25 && orphan.Width == 430 && orphan.Height == 510,
        "Window geometry must survive normalization.");
    Assert(orphan.ModifiedAt != default, "Legacy notes should receive a modified timestamp.");
    Assert(snapshot.Settings.NewNoteHotkey == "Ctrl+Shift+N" && snapshot.Settings.ManagerHotkey == "Ctrl+Shift+B",
        "Missing shortcut values should normalize to defaults.");
    Assert(!snapshot.Settings.NewNoteHotkeyEnabled, "Shortcut enabled state should survive normalization.");
    return Task.CompletedTask;
}

static async Task TestStore()
{
    var root = Environment.GetEnvironmentVariable("PINNOTE_TEST_TEMP")
        ?? throw new InvalidOperationException("PINNOTE_TEST_TEMP must point to the project temp directory.");
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "notes.json");
        var store = new JsonNoteStore(path);
        var snapshot = new NoteSnapshot
        {
            Groups = [new NoteGroup { Name = "项目" }],
            Notes = [new NoteDocument { Title = "中文便签", RtfContent = @"{\rtf1 hello}", IsHidden = true, Left = 222, Top = 333 }]
        };
        snapshot.Settings.NewNoteHotkey = "Ctrl+Alt+J";
        snapshot.Settings.ManagerHotkeyEnabled = false;
        snapshot.Notes[0].GroupId = snapshot.Groups[0].Id;

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();
        Assert(loaded.Notes.Count == 1 && loaded.Notes[0].Title == "中文便签", "Saved data should round-trip.");
        Assert(loaded.Notes[0].IsHidden && loaded.Notes[0].Left == 222 && loaded.Notes[0].Top == 333,
            "Visibility and geometry should round-trip.");
        Assert(loaded.Groups.Count == 1 && loaded.Notes[0].GroupId == loaded.Groups[0].Id, "Groups should round-trip.");
        Assert(loaded.Settings.NewNoteHotkey == "Ctrl+Alt+J" && !loaded.Settings.ManagerHotkeyEnabled,
            "Shortcut settings should round-trip.");

        loaded.Notes[0].Title = "Second save";
        await store.SaveAsync(loaded);
        Assert(File.Exists(path + ".bak"), "Replacing an existing store should retain one backup.");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary files should be cleaned up.");

        await File.WriteAllTextAsync(path, "{ broken json");
        var recovered = await store.LoadAsync();
        Assert(recovered.Notes[0].Title == "中文便签", "A corrupt primary store should recover from the last backup.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static NoteDocument NewNote(DateTimeOffset due, ReminderState state, ReminderLevel level = ReminderLevel.Normal) => new()
{
    ReminderAt = due,
    ReminderState = state,
    ReminderLevel = level
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
