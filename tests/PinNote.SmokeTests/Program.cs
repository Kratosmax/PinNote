using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Core.Storage;
using PinNote.Core.Updates;

var tests = new (string Name, Func<Task> Run)[]
{
    ("planner selects due and next reminder", TestPlanner),
    ("reminder transitions preserve overdue state", TestTransitions),
    ("todo hierarchy, reminders, and completion normalize safely", TestTodos),
    ("todo drag reorder and reparent reject hierarchy cycles", TestTodoMove),
    ("trash lifecycle, restore, duplicate, and purge are safe", TestItemLifecycle),
    ("snooze presets calculate precise due times", TestSnoozePlanner),
    ("snapshot clone is independent", TestClone),
    ("schema normalization preserves groups and note state", TestSchema),
    ("favorite text colors normalize and persist", TestFavoriteTextColors),
    ("update network settings normalize safely", TestUpdateNetworkSettings),
    ("update routes preserve priority and allowlist", TestUpdateRoutes),
    ("json store round-trips and creates backup", TestStore),
    ("signed update manifest rejects tampering", TestUpdateManifest),
    ("update channels reject cross-channel manifests", TestUpdateChannels),
    ("bounded copy handles non-seekable streams", TestBoundedStream),
    ("download staging releases and cleans temporary files", TestUpdatePackageStaging),
    ("real update package validates and installs", TestUpdatePackage),
    ("full update package accepts single-file updater", TestFullUpdatePackage),
    ("failed update restores existing files", TestUpdateRollback)
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

static Task TestTodos()
{
    var now = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.FromHours(8));
    var group = new TodoGroup
    {
        Name = "  发布  ",
        Left = 245,
        Top = 180,
        Width = 520,
        Height = 610,
        PinMode = PinMode.AlwaysOnTop,
        IsHidden = true
    };
    var parent = new TodoItem { GroupId = group.Id, Title = "父任务" };
    var child = new TodoItem
    {
        GroupId = group.Id,
        ParentId = parent.Id,
        Title = "子任务",
        ReminderAt = now.AddMinutes(-2),
        ReminderLevel = ReminderLevel.Strong
    };
    var grandchild = new TodoItem { GroupId = group.Id, ParentId = child.Id, Title = "孙任务", ReminderAt = now.AddMinutes(10) };
    var completed = new TodoItem
    {
        GroupId = group.Id,
        Title = "已办",
        ReminderAt = now.AddMinutes(-5),
        IsCompleted = true
    };
    var snapshot = new NoteSnapshot
    {
        SchemaVersion = 2,
        TodoGroups = [group],
        TodoItems = [parent, child, grandchild, completed]
    };
    snapshot.Settings.AutoCompleteParentTodo = true;
    snapshot.Normalize();

    Assert(snapshot.SchemaVersion == 5 && group.Name == "发布", "Todo data should upgrade to schema 5 and normalize group names.");
    Assert(group.Left == 245 && group.Top == 180 && group.Width == 520 && group.Height == 610 &&
           group.PinMode == PinMode.AlwaysOnTop && group.IsHidden,
        "Todo group window geometry, pin mode, and visibility should survive normalization.");
    Assert(TodoPlanner.GetDue(snapshot.TodoItems, now).SequenceEqual([child]), "Only incomplete scheduled overdue todos should trigger.");
    Assert(TodoPlanner.GetNextDue(snapshot.TodoItems, now) == grandchild.ReminderAt, "The nearest future todo reminder should be selected.");
    TodoPlanner.Trigger(child, now);
    Assert(child.ReminderState == ReminderState.Triggered && child.ReminderAt == now.AddMinutes(-2),
        "Strong todo reminders should remain actionable and retain the original overdue time.");
    TodoPlanner.Snooze(child, now.AddMinutes(5));
    Assert(child.ReminderState == ReminderState.Scheduled && child.ReminderLevel == ReminderLevel.Strong,
        "Snoozing a todo should preserve its reminder strength.");
    TodoPlanner.SetCompleted(child, true, now);
    Assert(child.IsCompleted && child.CompletedAt == now && !child.IsOverdue(now), "Completing a todo should retain completion evidence and clear overdue emphasis.");
    Assert(TodoPlanner.Descendants(snapshot.TodoItems, parent.Id).Select(item => item.Id).ToHashSet()
        .SetEquals([child.Id, grandchild.Id]), "Descendant traversal should include child and grandchild items.");

    TodoPlanner.SetCompleted(grandchild, true, now);
    var completedParents = TodoPlanner.CompleteEligibleAncestors(snapshot.TodoItems, grandchild, now, _ => true);
    Assert(completedParents.Select(item => item.Id).SequenceEqual([parent.Id]),
        "Completing the final descendant should make the eligible parent completable.");

    var clone = snapshot.Clone();
    clone.TodoGroups[0].Name = "Changed";
    clone.TodoItems[0].Title = "Changed";
    clone.Settings.AutoCompleteParentTodo = false;
    Assert(snapshot.TodoGroups[0].Name == "发布" && snapshot.TodoItems[0].Title == "父任务" &&
           snapshot.TodoGroups[0].PinMode == PinMode.AlwaysOnTop &&
           snapshot.Settings.AutoCompleteParentTodo, "Todo groups, items, and settings must clone independently.");

    var cycleA = new TodoItem { GroupId = group.Id };
    var cycleB = new TodoItem { GroupId = group.Id, ParentId = cycleA.Id };
    cycleA.ParentId = cycleB.Id;
    snapshot.TodoItems = [cycleA, cycleB];
    snapshot.Normalize();
    Assert(snapshot.TodoItems.Any(item => item.ParentId is null), "Cyclic todo parents should be broken during normalization.");
    return Task.CompletedTask;
}
static Task TestTodoMove()
{
    var group = new TodoGroup { Name = "拖放" };
    var first = new TodoItem { GroupId = group.Id, Title = "第一项", SortOrder = 0 };
    var second = new TodoItem { GroupId = group.Id, Title = "第二项", SortOrder = 1 };
    var third = new TodoItem { GroupId = group.Id, Title = "第三项", SortOrder = 2 };
    var child = new TodoItem { GroupId = group.Id, ParentId = first.Id, Title = "子项", SortOrder = 0 };
    var items = new List<TodoItem> { first, second, third, child };

    Assert(TodoPlanner.Move(items, third, first, makeChild: false), "A root todo should move before another root todo.");
    Assert(third.ParentId is null && third.SortOrder == 0 && first.SortOrder == 1 && second.SortOrder == 2,
        "Root siblings should be renumbered after a reorder.");
    var deletedSibling = new TodoItem { GroupId = group.Id, ParentId = first.Id, Title = "已删除", SortOrder = 99, DeletedAt = DateTimeOffset.Now };
    items.Add(deletedSibling);
    Assert(TodoPlanner.Move(items, second, first, makeChild: true), "A todo should become the target todo's last child.");
    Assert(second.ParentId == first.Id && child.SortOrder == 0 && second.SortOrder == 1 && deletedSibling.SortOrder == 99,
        "Reparenting should preserve existing children and append the dragged todo.");
    Assert(!TodoPlanner.Move(items, first, child, makeChild: true), "A parent cannot be moved under its descendant.");
    Assert(first.ParentId is null, "A rejected cyclic move must not mutate the parent id.");
    TodoPlanner.SetCompleted(child, true, DateTimeOffset.Now);
    TodoPlanner.SetCompleted(second, true, DateTimeOffset.Now);
    var completedParents = TodoPlanner.CompleteEligibleAncestors(items, child, DateTimeOffset.Now, _ => true);
    Assert(completedParents.Contains(first), "Deleted incomplete children must not block parent completion.");
    return Task.CompletedTask;
}
static Task TestItemLifecycle()
{
    var now = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.FromHours(8));
    var group = new TodoGroup { Name = "测试" };
    var note = NewNote(now.AddMinutes(-1), ReminderState.Scheduled);
    var parent = new TodoItem { GroupId = group.Id, Title = "父任务", IsCompleted = true, CompletedAt = now.AddHours(-1), ReminderAt = now.AddMinutes(-2) };
    var child = new TodoItem { GroupId = group.Id, ParentId = parent.Id, Title = "子任务", IsCompleted = true, CompletedAt = now.AddHours(-1) };
    var snapshot = new NoteSnapshot { Notes = [note], TodoGroups = [group], TodoItems = [parent, child] };

    ItemLifecycle.MoveToTrash(note, now);
    var deletedIds = ItemLifecycle.MoveTodoTreeToTrash(snapshot.TodoItems, parent, now);
    Assert(note.DeletedAt == now && deletedIds.SetEquals([parent.Id, child.Id]), "Deleting should soft-delete a note and its entire todo subtree.");
    Assert(ReminderPlanner.GetDue(snapshot.Notes, now).Count == 0 && TodoPlanner.GetDue(snapshot.TodoItems, now).Count == 0,
        "Deleted objects must never trigger reminders.");

    ItemLifecycle.Restore(note);
    Assert(note.DeletedAt is null && note.IsHidden && note.ReminderState == ReminderState.Scheduled,
        "Restoring a note should keep it hidden and re-arm its reminder.");
    var restored = ItemLifecycle.RestoreTodoTree(snapshot, parent);
    Assert(restored.Count == 2 && restored.All(item => item.DeletedAt is null),
        "Restoring a todo should restore its subtree.");

    var copies = ItemLifecycle.DuplicateTodoTree(snapshot.TodoItems, parent);
    Assert(copies.Count == 2 && copies.All(item => !item.IsCompleted && item.CompletedAt is null && item.DeletedAt is null),
        "Duplicated todo trees should preserve hierarchy while resetting completion and deletion state.");
    Assert(copies.Select(item => item.Id).Intersect([parent.Id, child.Id]).Count() == 0,
        "Duplicated todos must receive new ids.");
    Assert(copies.Single(item => item.ParentId is null).Title.EndsWith("副本", StringComparison.Ordinal),
        "The duplicate root should be identifiable and remain a root.");
    Assert(parent.IsCompleted && child.IsCompleted, "Duplicating must not mutate the source todo tree.");

    var missingGroupId = Guid.NewGuid();
    var orphanedDeletedTodo = new TodoItem { GroupId = missingGroupId, Title = "分组已删除", DeletedAt = now };
    var reloaded = new NoteSnapshot { TodoItems = [orphanedDeletedTodo] };
    reloaded.Normalize();
    Assert(reloaded.TodoItems.Contains(orphanedDeletedTodo), "Deleted todos must survive reload after their group is removed.");
    ItemLifecycle.RestoreTodoTree(reloaded, orphanedDeletedTodo);
    Assert(orphanedDeletedTodo.DeletedAt is null &&
           reloaded.TodoGroups.Single().Name == "已恢复待办" &&
           orphanedDeletedTodo.GroupId == reloaded.TodoGroups.Single().Id,
        "Restoring an orphaned deleted todo should create and use the recovered group.");

    var expired = new NoteDocument { Title = "过期", DeletedAt = now.AddDays(-31) };
    var retained = new NoteDocument { Title = "保留", DeletedAt = now.AddDays(-29) };
    snapshot.Notes.Add(expired);
    snapshot.Notes.Add(retained);
    var purged = ItemLifecycle.PurgeExpired(snapshot, now, 30);
    Assert(purged == 1 && !snapshot.Notes.Contains(expired) && snapshot.Notes.Contains(retained),
        "Startup purge should remove only items older than the configured retention.");
    return Task.CompletedTask;
}

static Task TestSnoozePlanner()
{
    var now = new DateTimeOffset(2026, 8, 21, 22, 15, 20, TimeSpan.FromHours(8));
    Assert(SnoozePlanner.GetDue(SnoozePreset.FiveMinutes, now) == now.AddMinutes(5), "Five-minute snooze should be exact.");
    Assert(SnoozePlanner.GetDue(SnoozePreset.ThirtyMinutes, now) == now.AddMinutes(30), "Thirty-minute snooze should be exact.");
    Assert(SnoozePlanner.GetDue(SnoozePreset.OneHour, now) == now.AddHours(1), "One-hour snooze should be exact.");
    var tomorrow = SnoozePlanner.GetDue(SnoozePreset.TomorrowMorning, now);
    Assert(tomorrow.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(1) &&
           tomorrow.LocalDateTime.TimeOfDay == TimeSpan.FromHours(9),
        "Tomorrow-morning snooze should target 09:00 local time.");
    return Task.CompletedTask;
}
static Task TestClone()
{
    var snapshot = new NoteSnapshot { Notes = [new NoteDocument { Title = "Original" }] };
    snapshot.Groups.Add(new NoteGroup { Name = "Work" });
    snapshot.Notes[0].GroupId = snapshot.Groups[0].Id;
    snapshot.Notes[0].IsHidden = true;
    snapshot.Notes[0].DeletedAt = DateTimeOffset.Now.AddDays(-2);
    snapshot.Settings.RecycleBinRetentionDays = 45;
    var clone = snapshot.Clone();
    clone.Notes[0].Title = "Changed";
    clone.Settings.EnableMaterial = false;
    clone.Settings.NewNoteHotkey = "Ctrl+Alt+N";
    clone.Settings.ManagerHotkeyEnabled = false;
    clone.Settings.AutoUpdateEnabled = false;
    clone.Settings.SkippedUpdateVersion = "0.4.0";
    clone.Settings.RememberFavoriteTextColor("#6B5BD2");
    Assert(snapshot.Notes[0].Title == "Original", "Cloned notes must be independent.");
    Assert(snapshot.Settings.EnableMaterial, "Cloned settings must be independent.");
    Assert(snapshot.Settings.NewNoteHotkey == "Ctrl+Shift+N" && snapshot.Settings.ManagerHotkeyEnabled,
        "Cloned shortcut settings must be independent.");
    Assert(snapshot.Settings.AutoUpdateEnabled && snapshot.Settings.SkippedUpdateVersion.Length == 0,
        "Cloned update settings must be independent.");
    Assert(snapshot.Settings.FavoriteTextColors.Count == 0, "Cloned favorite colors must be independent.");
    clone.Groups[0].Name = "Changed group";
    Assert(snapshot.Groups[0].Name == "Work", "Cloned groups must be independent.");
    Assert(clone.Notes[0].IsHidden && clone.Notes[0].GroupId == clone.Groups[0].Id && clone.Notes[0].DeletedAt == snapshot.Notes[0].DeletedAt,
        "Clone must preserve note management and deletion state.");
    Assert(clone.Settings.RecycleBinRetentionDays == 45, "Clone must preserve recycle-bin retention.");
    return Task.CompletedTask;
}

static Task TestSchema()
{
    Assert(new AppSettings().RecycleBinRetentionDays == 30, "Recycle-bin retention should default to 30 days.");
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

    Assert(snapshot.SchemaVersion == 5, "Old snapshots should normalize to the current schema.");
    Assert(known.Name == "工作", "Group names should normalize.");
    Assert(orphan.GroupId is null, "Unknown group references should move to ungrouped.");
    Assert(orphan.IsHidden, "Hidden state must survive normalization.");
    Assert(orphan.Left == 321.5 && orphan.Top == 176.25 && orphan.Width == 430 && orphan.Height == 510,
        "Window geometry must survive normalization.");
    Assert(orphan.ModifiedAt != default, "Legacy notes should receive a modified timestamp.");
    Assert(snapshot.Settings.NewNoteHotkey == "Ctrl+Shift+N" && snapshot.Settings.ManagerHotkey == "Ctrl+Shift+B",
        "Missing shortcut values should normalize to defaults.");
    Assert(!snapshot.Settings.NewNoteHotkeyEnabled, "Shortcut enabled state should survive normalization.");
    Assert(snapshot.Settings.AutoUpdateEnabled, "Legacy settings should enable automatic update checks by default.");
    snapshot.Settings.RecycleBinRetentionDays = 0;
    snapshot.Settings.Normalize();
    Assert(snapshot.Settings.RecycleBinRetentionDays == 1, "Recycle-bin retention should clamp to at least one day.");
    snapshot.Settings.RecycleBinRetentionDays = int.MaxValue;
    snapshot.Settings.Normalize();
    Assert(snapshot.Settings.RecycleBinRetentionDays == 3650, "Recycle-bin retention should clamp to the supported maximum.");
    return Task.CompletedTask;
}

static Task TestFavoriteTextColors()
{
    var settings = new AppSettings
    {
        FavoriteTextColors = [" #abcdef ", "invalid", "#ABCDEF", "#147D76", "#123456", "#654321", "#FEDCBA"]
    };
    settings.Normalize();
    Assert(settings.FavoriteTextColors.SequenceEqual(["#ABCDEF", "#123456", "#654321"]),
        "Favorite colors should normalize, de-duplicate, exclude permanent colors, and keep three slots.");
    Assert(settings.RememberFavoriteTextColor("#fedcba"), "A new favorite color should be remembered.");
    Assert(settings.FavoriteTextColors.SequenceEqual(["#FEDCBA", "#ABCDEF", "#123456"]),
        "The newest favorite should move to the first slot and evict the oldest.");
    Assert(!settings.RememberFavoriteTextColor("#202428"), "Permanent colors should not consume favorite slots.");
    Assert(!settings.RememberFavoriteTextColor("not-a-color"), "Invalid colors should be ignored.");
    return Task.CompletedTask;
}

static Task TestUpdateNetworkSettings()
{
    var settings = new UpdateNetworkSettings(
    [
        new GithubProxySetting("https://mirror.example/github/", 5),
        new GithubProxySetting("https://MIRROR.example/github", 8),
        new GithubProxySetting(string.Empty, 0, true),
        new GithubProxySetting(string.Empty, 10, true)
    ], "http://127.0.0.1:7890").Normalize();
    Assert(settings.GithubProxies!.Count == 2, "Duplicate proxies and duplicate direct routes should collapse.");
    Assert(settings.GithubProxies.Count(item => item.IsDirect) == 1, "Exactly one direct route must remain.");
    Assert(settings.GithubProxies.Single(item => !item.IsDirect).BaseUrl == "https://mirror.example/github",
        "Proxy trailing slashes should normalize.");
    Assert(settings.HttpProxy == "http://127.0.0.1:7890", "HTTP proxy authority should normalize.");
    Assert(!UpdateNetworkSettings.TryNormalizeGithubProxy("https://user:secret@proxy.example/path", out _),
        "Proxy credentials must be rejected.");
    Assert(!UpdateNetworkSettings.TryNormalizeGithubProxy("https://proxy.example/?token=secret", out _),
        "Proxy query strings must be rejected.");
    Assert(!UpdateNetworkSettings.TryNormalizeHttpProxy("https://127.0.0.1:7890", out _),
        "Only the supported HTTP proxy scheme should be accepted.");
    Assert(!UpdateNetworkSettings.TryNormalizeHttpProxy("http://127.0.0.1:7890/path", out _),
        "HTTP proxy paths must be rejected.");
    return Task.CompletedTask;
}

static Task TestUpdateRoutes()
{
    var original = new Uri("https://github.com/Kratosmax/PinNote/releases/latest/download/update.json");
    var settings = new UpdateNetworkSettings(
    [
        new GithubProxySetting(string.Empty, 3, true),
        new GithubProxySetting("https://first.example", 7),
        new GithubProxySetting("https://second.example/prefix", 7),
        new GithubProxySetting("https://disabled.example", 0)
    ]);
    var routes = UpdateRouteBuilder.Build(original, settings);
    Assert(routes.Count == 3, "Priority zero routes should be disabled.");
    Assert(routes[0].RequestUri.Host == "first.example" && routes[1].RequestUri.Host == "second.example",
        "Equal-priority proxy routes should preserve list order.");
    Assert(routes[2].IsDirect && routes[2].RequestUri == original, "Lower-priority direct should remain as fallback.");
    Assert(routes[1].RequestUri.AbsoluteUri == $"https://second.example/prefix/{original.AbsoluteUri}",
        "Path prefixes should compose deterministically.");

    var external = new Uri("https://example.com/update.json");
    var externalRoutes = UpdateRouteBuilder.Build(external, settings);
    Assert(externalRoutes.Count == 1 && externalRoutes[0].RequestUri == external,
        "Non-GitHub URLs must never be sent through prefix proxies.");
    var disabled = new UpdateNetworkSettings([new GithubProxySetting(string.Empty, 0, true)]);
    Assert(UpdateRouteBuilder.Build(original, disabled).Count == 0,
        "The routing layer must reject an all-disabled configuration with no candidate requests.");
    return Task.CompletedTask;
}

static Task TestUpdateManifest()
{
    using var rsa = RSA.Create(2048);
    var unsigned = new UpdateManifest
    {
        Version = "0.4.1",
        Channel = UpdateTrust.Channel,
        DownloadUrl = "https://github.com/Kratosmax/PinNote/releases/download/v0.4.1/PinNote-0.4.1-Lite-Portable.zip",
        Size = 12345,
        Sha256 = new string('A', 64),
        ReleaseNotes = "安全更新"
    };
    var signature = UpdateManifestCodec.Sign(unsigned, rsa.ExportPkcs8PrivateKeyPem());
    var signed = new UpdateManifest
    {
        Version = unsigned.Version,
        Channel = unsigned.Channel,
        DownloadUrl = unsigned.DownloadUrl,
        Size = unsigned.Size,
        Sha256 = unsigned.Sha256,
        Signature = signature,
        ReleaseNotes = unsigned.ReleaseNotes
    };
    var json = UpdateManifestCodec.Serialize(signed);
    var parsed = UpdateManifestCodec.ParseAndVerify(json, rsa.ExportSubjectPublicKeyInfoPem(), UpdateTrust.Channel);
    Assert(parsed.Version == new Version(0, 4, 1), "A valid signed manifest should parse.");

    var tampered = json.Replace("12345", "12346", StringComparison.Ordinal);
    AssertThrows<CryptographicException>(
        () => UpdateManifestCodec.ParseAndVerify(tampered, rsa.ExportSubjectPublicKeyInfoPem(), UpdateTrust.Channel),
        "Changing signed fields must invalidate the signature.");
    return Task.CompletedTask;
}

static Task TestUpdateChannels()
{
    using var rsa = RSA.Create(2048);
    var unsigned = new UpdateManifest
    {
        Version = "0.4.1",
        Channel = UpdateTrust.FullChannel,
        DownloadUrl = "https://github.com/Kratosmax/PinNote/releases/download/v0.4.1/PinNote-0.4.1-Full-Portable.zip",
        Size = 12345,
        Sha256 = new string('B', 64)
    };
    var signed = new UpdateManifest
    {
        Version = unsigned.Version,
        Channel = unsigned.Channel,
        DownloadUrl = unsigned.DownloadUrl,
        Size = unsigned.Size,
        Sha256 = unsigned.Sha256,
        Signature = UpdateManifestCodec.Sign(unsigned, rsa.ExportPkcs8PrivateKeyPem())
    };
    var json = UpdateManifestCodec.Serialize(signed);
    AssertThrows<InvalidDataException>(
        () => UpdateManifestCodec.ParseAndVerify(json, rsa.ExportSubjectPublicKeyInfoPem(), UpdateTrust.LiteChannel),
        "A Full manifest must not be accepted by a Lite installation.");
    return Task.CompletedTask;
}

static async Task TestBoundedStream()
{
    var bytes = Encoding.UTF8.GetBytes(new string('x', 128));
    await using var source = new NonSeekableReadStream(bytes);
    await using var destination = new MemoryStream();
    Assert(!source.CanSeek, "The regression source must remain non-seekable.");
    await AssertThrowsAsync<InvalidDataException>(
        () => BoundedStream.CopyToAsync(source, destination, 64),
        "Copying beyond the configured limit must fail.");
}

static async Task TestUpdatePackage()
{
    var root = CreateTestDirectory();
    try
    {
        var version = CurrentTestVersion();
        var packagePath = await CreatePackageAsync(root, version);
        var update = await CreateUpdateInfoAsync(packagePath, version);
        var validated = await UpdatePackageValidator.ValidateAsync(packagePath, update);
        Assert(validated.Metadata.Version == version.ToString(3), "Package metadata should match the signed version.");

        var target = Path.Combine(root, "install");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "PinNote.exe"), "old");
        await WriteMetadataAsync(Path.Combine(target, "pinnote-install.json"), version);
        await UpdateInstaller.InstallAsync(packagePath, target, update);
        Assert(await File.ReadAllTextAsync(Path.Combine(target, "PinNote.exe")) == "new", "The candidate executable should replace the old file.");
        Assert(File.Exists(Path.Combine(target, "PinNote.dll")), "The candidate assembly should be installed.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestUpdatePackageStaging()
{
    var root = CreateTestDirectory();
    try
    {
        var version = CurrentTestVersion();
        var sourcePackage = await CreatePackageAsync(root, version);
        var update = await CreateUpdateInfoAsync(sourcePackage, version);
        var temporaryPath = Path.Combine(root, "package.zip.download");
        var packagePath = Path.Combine(root, "package.zip");
        var progress = new Progress<int>();

        await using (var incomplete = new MemoryStream(new byte[32]))
        {
            await AssertThrowsAsync<EndOfStreamException>(
                () => UpdatePackageStager.StageAsync(incomplete, temporaryPath, packagePath, update, progress),
                "An incomplete route should fail staging.");
        }
        Assert(!File.Exists(temporaryPath), "A failed route must not leave a locked temporary file.");

        await using (var source = File.OpenRead(sourcePackage))
        {
            await UpdatePackageStager.StageAsync(source, temporaryPath, packagePath, update, progress);
        }

        Assert(File.Exists(packagePath), "A verified download should be atomically renamed to package.zip.");
        Assert(!File.Exists(temporaryPath), "A successful download should not leave package.zip.download.");
        await UpdatePackageValidator.ValidateAsync(packagePath, update);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestFullUpdatePackage()
{
    var root = CreateTestDirectory();
    try
    {
        var version = CurrentTestVersion();
        var packagePath = await CreatePackageAsync(root, version, channel: UpdateTrust.FullChannel);
        var update = await CreateUpdateInfoAsync(packagePath, version, UpdateTrust.FullChannel);
        var validated = await UpdatePackageValidator.ValidateAsync(packagePath, update);
        Assert(validated.Metadata.Channel == UpdateTrust.FullChannel, "Full package channel should be preserved.");
        Assert(!validated.Files.Contains("PinNote.Updater.dll"), "Full packages should support a single-file updater.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestUpdateRollback()
{
    var root = CreateTestDirectory();
    try
    {
        var version = CurrentTestVersion();
        var packagePath = await CreatePackageAsync(root, version, includeLockedFile: true);
        var update = await CreateUpdateInfoAsync(packagePath, version);
        var target = Path.Combine(root, "install");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "PinNote.exe"), "old");
        await File.WriteAllTextAsync(Path.Combine(target, "locked.txt"), "locked-old");
        await WriteMetadataAsync(Path.Combine(target, "pinnote-install.json"), version);

        await using var locked = new FileStream(Path.Combine(target, "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await AssertThrowsAsync<IOException>(
            () => UpdateInstaller.InstallAsync(packagePath, target, update),
            "A replacement failure should abort the transaction.");
        Assert(await File.ReadAllTextAsync(Path.Combine(target, "PinNote.exe")) == "old", "Rollback must restore the previous executable.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string CreateTestDirectory()
{
    var baseDirectory = Environment.GetEnvironmentVariable("PINNOTE_TEST_TEMP")
        ?? throw new InvalidOperationException("PINNOTE_TEST_TEMP must point to the project temp directory.");
    var directory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static Version CurrentTestVersion()
{
    var version = typeof(UpdateTrust).Assembly.GetName().Version
        ?? throw new InvalidOperationException("The test assembly has no version.");
    return new Version(version.Major, version.Minor, version.Build);
}

static async Task<string> CreatePackageAsync(
    string root,
    Version version,
    bool includeLockedFile = false,
    string channel = UpdateTrust.LiteChannel)
{
    var content = Path.Combine(root, "content-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(content);
    await File.WriteAllTextAsync(Path.Combine(content, "PinNote.exe"), "new");
    File.Copy(typeof(UpdateTrust).Assembly.Location, Path.Combine(content, "PinNote.dll"));
    await File.WriteAllTextAsync(Path.Combine(content, "PinNote.Updater.exe"), "updater");
    if (channel == UpdateTrust.LiteChannel)
    {
        await File.WriteAllTextAsync(Path.Combine(content, "PinNote.Updater.dll"), "updater");
        await File.WriteAllTextAsync(Path.Combine(content, "PinNote.Updater.deps.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(content, "PinNote.Updater.runtimeconfig.json"), "{}");
    }
    await WriteMetadataAsync(Path.Combine(content, "pinnote-install.json"), version, channel);
    await WriteMetadataAsync(Path.Combine(content, "pinnote-package.json"), version, channel);
    if (includeLockedFile)
    {
        await File.WriteAllTextAsync(Path.Combine(content, "locked.txt"), "locked-new");
    }
    var packagePath = Path.Combine(root, $"package-{Guid.NewGuid():N}.zip");
    ZipFile.CreateFromDirectory(content, packagePath, CompressionLevel.NoCompression, includeBaseDirectory: false);
    Directory.Delete(content, recursive: true);
    return packagePath;
}

static Task WriteMetadataAsync(string path, Version version, string channel = UpdateTrust.LiteChannel) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(new PinNotePackageMetadata
{
    Version = version.ToString(3),
    Channel = channel
}));

static async Task<UpdateInfo> CreateUpdateInfoAsync(
    string packagePath,
    Version version,
    string channel = UpdateTrust.LiteChannel)
{
    var file = new FileInfo(packagePath);
    await using var stream = file.OpenRead();
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
    return new UpdateInfo(
        version,
        channel,
        new Uri($"https://github.com/Kratosmax/PinNote/releases/download/v{version.ToString(3)}/package.zip"),
        file.Length,
        hash,
        string.Empty,
        string.Empty);
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
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
        snapshot.Settings.AutoCompleteParentTodo = true;
        snapshot.Settings.RememberFavoriteTextColor("#6B5BD2");
        snapshot.Notes[0].GroupId = snapshot.Groups[0].Id;
        snapshot.TodoGroups.Add(new TodoGroup
        {
            Name = "发布待办",
            Left = 410,
            Top = 220,
            Width = 460,
            Height = 540,
            PinMode = PinMode.AlwaysOnTop,
            IsHidden = true
        });
        snapshot.TodoItems.Add(new TodoItem
        {
            GroupId = snapshot.TodoGroups[0].Id,
            Title = "验证安装包",
            ReminderAt = new DateTimeOffset(2026, 8, 21, 10, 20, 30, TimeSpan.FromHours(8)),
            ReminderLevel = ReminderLevel.Ultra
        });

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();
        Assert(loaded.Notes.Count == 1 && loaded.Notes[0].Title == "中文便签", "Saved data should round-trip.");
        Assert(loaded.Notes[0].IsHidden && loaded.Notes[0].Left == 222 && loaded.Notes[0].Top == 333,
            "Visibility and geometry should round-trip.");
        Assert(loaded.Groups.Count == 1 && loaded.Notes[0].GroupId == loaded.Groups[0].Id, "Groups should round-trip.");
        Assert(loaded.Settings.NewNoteHotkey == "Ctrl+Alt+J" && !loaded.Settings.ManagerHotkeyEnabled,
            "Shortcut settings should round-trip.");
        Assert(loaded.Settings.FavoriteTextColors.SequenceEqual(["#6B5BD2"]),
            "Favorite text colors should round-trip.");
        Assert(loaded.TodoGroups.Count == 1 && loaded.TodoItems.Count == 1 &&
               loaded.TodoItems[0].GroupId == loaded.TodoGroups[0].Id &&
               loaded.TodoItems[0].ReminderAt?.Second == 30 &&
               loaded.TodoItems[0].ReminderLevel == ReminderLevel.Ultra &&
               loaded.TodoGroups[0].Left == 410 &&
               loaded.TodoGroups[0].PinMode == PinMode.AlwaysOnTop &&
               loaded.TodoGroups[0].IsHidden,
            "Todo group window state, reminder strength, and second-precision reminders should round-trip.");
        Assert(loaded.Settings.AutoCompleteParentTodo, "Todo parent completion behavior should round-trip.");

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

file sealed class NonSeekableReadStream(byte[] content) : Stream
{
    private readonly MemoryStream _inner = new(content, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
