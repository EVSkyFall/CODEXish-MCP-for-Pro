using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

public readonly record struct DesktopRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && (long)x < (long)X + Width && (long)y < (long)Y + Height;
    public bool Contains(DesktopRect r) => r.Width > 0 && r.Height > 0 && Contains(r.X, r.Y) &&
        (long)r.X + r.Width <= (long)X + Width && (long)r.Y + r.Height <= (long)Y + Height;
}
public sealed record DesktopWindow(string Id, long Handle, int Pid, long StartTicks, string Title,
    DesktopRect Bounds, bool Minimized, bool OwnProcess);
public sealed record DesktopScene(DesktopRect Bounds, string Layout, DesktopWindow[] Windows,
    string? Foreground, uint InputTick, int CursorX, int CursorY);
public sealed record DesktopElement(string Id, string Role, string Name, DesktopRect Bounds,
    bool Focused, bool Password, bool Enabled, bool Offscreen, bool Selected, string? Text);
public sealed record DesktopUi(DesktopElement[] Elements, string? Error = null);
public sealed record DesktopGeometry(DesktopRect Source, int Width, int Height)
{
    public static DesktopGeometry Create(DesktopRect source, int maximum)
    {
        if (maximum < 0 || source.Width <= 0 || source.Height <= 0)
            throw new CodexishFault("INVALID_ARGUMENT", "Positive capture dimensions and nonnegative max_width required.");
        int width = maximum == 0 ? source.Width : Math.Min(source.Width, maximum);
        int height = Math.Max(1, checked((int)Math.Round((double)source.Height * width / source.Width)));
        return new(source, width, height);
    }
    public (int x, int y) Point(int x, int y, string space)
    {
        if (space == "image")
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) throw new CodexishFault("INVALID_ARGUMENT", "Point is outside this image.");
            return (checked(Source.X + (int)((long)x * Source.Width / Width)), checked(Source.Y + (int)((long)y * Source.Height / Height)));
        }
        if (space != "desktop_physical_px" || !Source.Contains(x, y))
            throw new CodexishFault("INVALID_ARGUMENT", "Use image or desktop_physical_px within the captured rectangle.");
        return (x, y);
    }
    public object Describe() => new { source = Source, image = new { width = Width, height = Height },
        image_to_desktop = new { offset_x = Source.X, offset_y = Source.Y, scale_x = (double)Source.Width / Width,
            scale_y = (double)Source.Height / Height, rounding = "floor" } };
}

// A platform-independent input plan also makes prefix cleanup testable without injecting desktop input.
public sealed record DesktopInput(string Kind, int Code = 0, bool Up = false, int X = 0, int Y = 0);
public static class DesktopInputPlan
{
    private static readonly Dictionary<string, int> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12, ["WIN"] = 0x5b,
        ["ENTER"] = 13, ["RETURN"] = 13, ["TAB"] = 9, ["ESC"] = 27, ["ESCAPE"] = 27,
        ["SPACE"] = 32, ["BACKSPACE"] = 8, ["DELETE"] = 46, ["INSERT"] = 45,
        ["HOME"] = 36, ["END"] = 35, ["PAGEUP"] = 33, ["PAGEDOWN"] = 34,
        ["LEFT"] = 37, ["UP"] = 38, ["RIGHT"] = 39, ["DOWN"] = 40
    };
    public static DesktopInput[] Text(string text) => text.SelectMany(c => new[] {
        new DesktopInput("unicode", c), new DesktopInput("unicode", c, true) }).ToArray();
    public static DesktopInput[] Combo(string combo)
    {
        var values = combo.Split('+', StringSplitOptions.TrimEntries).Select(s =>
        {
            if (Keys.TryGetValue(s, out int value)) return value;
            if (s.Length == 1 && char.IsAsciiLetterOrDigit(s[0])) return (int)char.ToUpperInvariant(s[0]);
            if (s.StartsWith('F') && int.TryParse(s[1..], out int f) && f is >= 1 and <= 24) return 0x6f + f;
            throw new CodexishFault("INVALID_ARGUMENT", "Unknown key name; use CTRL+S, TAB, arrows, letters or F1-F24.");
        }).ToArray();
        if (values.Length == 0 || values.Distinct().Count() != values.Length)
            throw new CodexishFault("INVALID_ARGUMENT", "A key combination must contain distinct keys.");
        return values.Select(k => new DesktopInput("key", k)).Concat(values.Reverse().Select(k => new DesktopInput("key", k, true))).ToArray();
    }
    public static DesktopInput[] Releases(IReadOnlyList<DesktopInput> plan, int delivered)
    {
        if (delivered < 0 || delivered > plan.Count) throw new ArgumentOutOfRangeException(nameof(delivered));
        var down = new List<DesktopInput>();
        foreach (var item in plan.Take(delivered))
        {
            if (item.Kind is not ("key" or "unicode" or "left" or "right")) continue;
            if (item.Up) down.RemoveAll(x => x.Kind == item.Kind && x.Code == item.Code);
            else if (!down.Any(x => x.Kind == item.Kind && x.Code == item.Code)) down.Add(item);
        }
        down.Reverse(); return down.Select(x => x with { Up = true }).ToArray();
    }
}
public interface IDesktopPlatform
{
    DesktopScene Inspect();
    byte[] Capture(DesktopGeometry geometry);
    DesktopUi Query(DesktopWindow window);
    long HitTest(int x, int y);
    int Send(DesktopInput[] inputs, DesktopRect desktop);
    bool WindowAction(DesktopWindow window, string action);
}

public sealed class DesktopService : IDisposable
{
    private sealed record Observation(string Id, DesktopScene Scene, DesktopWindow? Target, DesktopGeometry Geometry);
    private sealed record QueryPage(string Observation, string? Role, string? Name, DesktopElement[] Elements, int Offset);
    private readonly IDesktopPlatform platform;
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private readonly Dictionary<string, Observation> observations = new();
    private readonly Dictionary<string, (string observation, DesktopElement element)> elements = new();
    private readonly Dictionary<string, QueryPage> pages = new();
    public DesktopService() : this(DesktopPlatform.Create()) { }
    public DesktopService(IDesktopPlatform platform)
    {
        this.platform = platform;
        thread = new Thread(() => { foreach (var action in work.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "CODEXish desktop / UIA" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    public Task<T> OnThread<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { work.Add(() => { try { result.SetResult(action()); } catch (Exception e) { result.SetException(e); } }); }
        catch (InvalidOperationException) { result.SetException(new ObjectDisposedException(nameof(DesktopService))); }
        return result.Task;
    }
    public Task<CallToolResult> Observe(string? windowId = null, int maxWidth = 1280, bool windowOnly = false,
        DesktopRect? crop = null) => OnThread(() => Reply.Guard(() => ObserveNow(windowId, maxWidth, windowOnly, crop)));
    private CallToolResult ObserveNow(string? windowId, int maxWidth, bool windowOnly, DesktopRect? crop)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var scene = platform.Inspect();
        string? selected = windowId ?? scene.Foreground;
        var target = selected is null ? null : scene.Windows.SingleOrDefault(w => w.Id == selected)
            ?? throw new CodexishFault("WINDOW_NOT_FOUND", "Selected window is no longer present; observe again.");
        if (target?.OwnProcess == true) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "The server's own control UI is not a target.");
        var rectangle = crop ?? (windowOnly ? target?.Bounds ?? throw new CodexishFault("WINDOW_NOT_FOUND", "A window is required.") : scene.Bounds);
        if (!scene.Bounds.Contains(rectangle)) throw new CodexishFault("INVALID_ARGUMENT", "Capture rectangle must be inside the virtual desktop; use an explicit visible crop for a partly offscreen window.");
        var geometry = DesktopGeometry.Create(rectangle, maxWidth);
        byte[] png = platform.Capture(geometry); DateTimeOffset captured = DateTimeOffset.UtcNow;
        DesktopUi ui = target is null ? new([]) : platform.Query(target);
        DateTimeOffset queried = DateTimeOffset.UtcNow;
        var after = platform.Inspect();
        var obs = new Observation("obs_" + Guid.NewGuid().ToString("N"), scene, target, geometry);
        bool consistent = Same(scene, after, target, false);
        observations.Add(obs.Id, obs);
        var data = new { observation_id = obs.Id, window_id = target?.Id, capture = geometry.Describe(),
            virtual_desktop = scene.Bounds, layout = scene.Layout, windows = scene.Windows,
            foreground_window_id = scene.Foreground, cursor = new { x = scene.CursorX, y = scene.CursorY },
            input_tick = scene.InputTick, input_tick_role = "metadata_only", stable_during_capture = consistent,
            timestamps = new { started, image_captured = captured, ui_sampled = queried }, atomic_snapshot = false,
            ui = new { error = ui.Error, element_count = ui.Elements.Length,
                focused = ui.Elements.Where(e => e.Focused).Select(e => PublicElement(obs.Id, e)).ToArray(),
                active_tabs = ui.Elements.Where(e => e.Role == "TabItem" && e.Selected).Select(e => e.Name).ToArray(),
                documents = ui.Elements.Where(e => e.Role == "Document").Select(e => e.Name).ToArray() },
            png_bytes = png.Length, next_tool = consistent ? "computer_query_ui or computer_act" : "computer_observe; screen changed while sampling" };
        return WithImage(Reply.Ok(data), png);
    }
    private object PublicElement(string observationId, DesktopElement item)
    {
        string id = observationId + ":" + item.Id;
        elements[id] = (observationId, item);
        return new { element_id = id, role = item.Role, name = item.Name, bounds = item.Bounds,
            focused = item.Focused, password = item.Password, enabled = item.Enabled, offscreen = item.Offscreen,
            selected = item.Selected, text = item.Password ? null : item.Text };
    }
    private Observation Get(string id) => observations.TryGetValue(id, out var obs) ? obs :
        throw new CodexishFault("STALE_OBSERVATION", "Observation is not from this server lifetime; capture a new one.");
    private static bool Same(DesktopScene expected, DesktopScene current, DesktopWindow? target, bool focusAction)
    {
        if (expected.Bounds != current.Bounds || expected.Layout != current.Layout) return false;
        if (!focusAction && expected.Foreground != current.Foreground) return false;
        if (target is null) return true;
        var now = current.Windows.SingleOrDefault(w => w.Id == target.Id);
        return now is not null && now.Handle == target.Handle && now.Pid == target.Pid && now.StartTicks == target.StartTicks &&
            now.Bounds == target.Bounds && now.Minimized == target.Minimized;
    }
    private DesktopWindow Validate(Observation obs, bool focusAction = false)
    {
        var target = obs.Target ?? throw new CodexishFault("WINDOW_NOT_FOUND", "Observation has no selected window.");
        if (target.OwnProcess) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Server control UI is excluded.");
        var scene = platform.Inspect();
        if (!Same(obs.Scene, scene, target, focusAction)) throw new CodexishFault("STALE_OBSERVATION", "Window identity, bounds, foreground or display layout changed; observe again.");
        if (!focusAction && (scene.Foreground != target.Id || target.Minimized))
            throw new CodexishFault("STALE_OBSERVATION", "Focus this window with focus_window and use its returned observation first.");
        return target;
    }
    public Task<CallToolResult> Query(string observationId, string? role, string? name, string? cursor, int pageSize) =>
        OnThread(() => Reply.Guard(() =>
        {
            if (pageSize is < 1 or > 200) throw new CodexishFault("INVALID_ARGUMENT", "page_size is 1..200, a response-page budget.");
            var obs = Get(observationId); Validate(obs, true);
            QueryPage page;
            if (cursor is not null)
            {
                if (!pages.TryGetValue(cursor, out page!) || page.Observation != observationId || page.Role != role || page.Name != name)
                    throw new CodexishFault("CURSOR_INVALID", "Cursor belongs to another observation or query.");
            }
            else
            {
                var ui = platform.Query(obs.Target!);
                if (ui.Error is not null) return Reply.Error("UNSUPPORTED_CAPABILITY", "UI Automation could not inspect this provider.", details: new { provider_error = ui.Error });
                page = new(observationId, role, name, ui.Elements.Where(e =>
                    (role is null || e.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) &&
                    (name is null || e.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || (!e.Password && e.Text?.Contains(name, StringComparison.OrdinalIgnoreCase) == true))).ToArray(), 0);
            }
            var result = page.Elements.Skip(page.Offset).Take(pageSize).Select(e => PublicElement(observationId, e)).ToArray();
            int nextOffset = page.Offset + result.Length; string? next = null;
            if (nextOffset < page.Elements.Length) { next = "ui_" + Guid.NewGuid().ToString("N"); pages.Add(next, page with { Offset = nextOffset }); }
            return Reply.Ok(new { observation_id = observationId, elements = result, total = page.Elements.Length,
                next_cursor = next, limits_source = "UI result page size; full query snapshot retained", next_tool = "computer_act click_element or continue cursor" });
        }));
    public Task<CallToolResult> Act(string observationId, string action, string space, int? x, int? y,
        int? endX, int? endY, string? elementId, string? text, string? key, int wheel, bool after) => OnThread(() =>
    {
        bool attempted = false; int delivered = 0; int planned = 0; int cleanup = 0; int cleanupSent = 0;
        try
        {
            var obs = Get(observationId);
            bool windowAction = action is "focus_window" or "minimize_window" or "maximize_window" or "restore_window";
            if (space is not ("image" or "desktop_physical_px" or "none")) throw new CodexishFault("INVALID_ARGUMENT", "coordinate_space must be image, desktop_physical_px or none.");
            var target = Validate(obs, windowAction);
            DesktopInput[] plan;
            if (windowAction)
            {
                if (space != "none") throw new CodexishFault("INVALID_ARGUMENT", "Window actions use coordinate_space=none.");
                attempted = true;
                if (!platform.WindowAction(target, action))
                    throw new CodexishFault("EXECUTION_UNKNOWN", "Windows did not confirm this window request; reobserve instead of forcing focus.", "unknown");
                plan = [];
            }
            else if (action is "type_text" or "key_combo" or "key_press")
            {
                if (space != "none") throw new CodexishFault("INVALID_ARGUMENT", "Keyboard actions use coordinate_space=none.");
                plan = action == "type_text" ? DesktopInputPlan.Text(text ?? throw new CodexishFault("INVALID_ARGUMENT", "text required.")) :
                    DesktopInputPlan.Combo(key ?? throw new CodexishFault("INVALID_ARGUMENT", "key required."));
            }
            else
            {
                (int px, int py) point;
                if (action == "click_element")
                {
                    if (space != "none" || elementId is null || !elements.TryGetValue(elementId, out var old) || old.observation != obs.Id)
                        throw new CodexishFault("STALE_OBSERVATION", "Use coordinate_space=none and an element_id from this observation.");
                    var current = platform.Query(target).Elements.SingleOrDefault(e => e.Id == old.element.Id);
                    if (current is null || current.Bounds != old.element.Bounds || !current.Enabled || current.Offscreen)
                        throw new CodexishFault("STALE_OBSERVATION", "UI element moved, disappeared or is not interactable.");
                    point = (current.Bounds.X + current.Bounds.Width / 2, current.Bounds.Y + current.Bounds.Height / 2);
                }
                else point = obs.Geometry.Point(x ?? throw new CodexishFault("INVALID_ARGUMENT", "x required."), y ?? throw new CodexishFault("INVALID_ARGUMENT", "y required."), space);
                void Hit((int px, int py) p)
                {
                    if (!target.Bounds.Contains(p.px, p.py) || !obs.Scene.Bounds.Contains(p.px, p.py) || platform.HitTest(p.px, p.py) != target.Handle)
                        throw new CodexishFault("STALE_OBSERVATION", "The selected window does not own this visible point.");
                }
                Hit(point); var events = new List<DesktopInput> { new("move", X: point.px, Y: point.py) };
                switch (action)
                {
                    case "click_coordinate": case "click_element": events.Add(new("left")); events.Add(new("left", Up: true)); break;
                    case "double_click": events.AddRange([new("left"), new("left", Up: true), new("left"), new("left", Up: true)]); break;
                    case "right_click": events.Add(new("right")); events.Add(new("right", Up: true)); break;
                    case "move": break;
                    case "scroll": events.Add(new("wheel", wheel)); break;
                    case "drag":
                        var end = obs.Geometry.Point(endX ?? throw new CodexishFault("INVALID_ARGUMENT", "end_x required."), endY ?? throw new CodexishFault("INVALID_ARGUMENT", "end_y required."), space);
                        Hit(end); events.Add(new("left"));
                        for (int i = 1; i <= 16; i++) events.Add(new("move", X: (int)(point.px + ((long)end.x - point.px) * i / 16), Y: (int)(point.py + ((long)end.y - point.py) * i / 16)));
                        events.Add(new("left", Up: true)); break;
                    default: throw new CodexishFault("INVALID_ARGUMENT", "Unsupported action name.");
                }
                plan = events.ToArray();
            }
            if (plan.Length > 0)
            {
                Validate(obs); // Recheck after potentially slow UIA lookup, immediately before input.
                attempted = true; planned = plan.Length; delivered = platform.Send(plan, obs.Scene.Bounds);
                if (delivered != plan.Length)
                {
                    var release = DesktopInputPlan.Releases(plan, delivered); cleanup = release.Length;
                    if (cleanup > 0) cleanupSent = platform.Send(release, obs.Scene.Bounds);
                    throw new CodexishFault(delivered == 0 ? "EXECUTION_FAILED" : "EXECUTION_UNKNOWN",
                        "Input was blocked or partially inserted; do not replay. Inspect the next observation.", delivered == 0 ? "none" : "partial");
                }
            }
            CallToolResult? post = after ? ObserveNow(target.Id, obs.Geometry.Width, false, obs.Geometry.Source) : null;
            var ok = Reply.Ok(new { action, delivered, planned, side_effects = attempted ? "applied" : "none",
                business_outcome = "not_verified", observation = post?.StructuredContent, next_tool = "inspect observation; verify saved file/test outcome separately" });
            return post is null ? ok : AppendImages(ok, post);
        }
        catch (Exception e)
        {
            var fault = e as CodexishFault;
            string effects = fault?.Effects ?? (attempted ? "unknown" : "none");
            if (attempted && fault?.Code is "WINDOW_NOT_FOUND" or "STALE_OBSERVATION") effects = "applied";
            return Reply.Error(attempted && effects != "none" ? "EXECUTION_UNKNOWN" : fault?.Code ?? "EXECUTION_FAILED",
                fault?.Message ?? "Desktop provider failed; inspect the effect before retrying.", effects,
                details: new { cause = fault?.Code ?? e.GetType().Name, delivered, planned, cleanup_planned = cleanup, cleanup_sent = cleanupSent },
                recovery: "computer_observe; never replay uncertain input");
        }
    });
    private static CallToolResult WithImage(CallToolResult result, byte[] png) => new()
    {
        IsError = result.IsError, StructuredContent = result.StructuredContent,
        Content = [..result.Content, ImageContentBlock.FromBytes(png, "image/png")]
    };
    private static CallToolResult AppendImages(CallToolResult result, CallToolResult source) => new()
    {
        IsError = result.IsError, StructuredContent = result.StructuredContent,
        Content = [..result.Content, ..source.Content.OfType<ImageContentBlock>()]
    };
    public void Dispose() { work.CompleteAdding(); thread.Join(); work.Dispose(); }
}

[McpServerToolType]
public sealed class DesktopTools(CodexishRuntime runtime, DesktopService desktop)
{
    [McpServerTool(Name = "computer_observe", ReadOnly = true, OpenWorld = false)]
    [Description("Observe real Windows pixels, window identity, virtual-desktop coordinates and focused control/active-tab UIA metadata. max_width=0 keeps native resolution. Use window_only or crop for detail. Preserve observation_id; query_ui for semantic targets, act for one action. InputTick is metadata, not a stale lock. Capture and UIA are sampled at different times.")]
    public Task<CallToolResult> Observe(string? window_id = null, int max_width = 1280, bool window_only = false,
        int? crop_x = null, int? crop_y = null, int? crop_width = null, int? crop_height = null)
    {
        bool any = crop_x.HasValue || crop_y.HasValue || crop_width.HasValue || crop_height.HasValue;
        if (any && !(crop_x.HasValue && crop_y.HasValue && crop_width.HasValue && crop_height.HasValue))
            return Task.FromResult(Reply.Error("INVALID_ARGUMENT", "Specify all four crop coordinates."));
        return desktop.Observe(window_id, max_width, window_only, any ? new(crop_x!.Value, crop_y!.Value, crop_width!.Value, crop_height!.Value) : null);
    }
    [McpServerTool(Name = "computer_query_ui", ReadOnly = true, OpenWorld = false)]
    [Description("Query the selected window's UI Automation elements by role/name/text. Returns element_id bound to observation, focus, selected tabs and physical bounds. Password values are omitted. Page with next_cursor; stale/provider failures require another observation, not a guessed click.")]
    public Task<CallToolResult> Query(string observation_id, string? role = null, string? name = null, string? cursor = null, int page_size = 100) =>
        desktop.Query(observation_id, role, name, cursor, page_size);
    [McpServerTool(Name = "computer_act", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Perform one Windows action then observe by default. Actions: click_element, click_coordinate, double_click, right_click, move, scroll, drag, type_text, key_combo, key_press, focus_window, minimize_window, maximize_window, restore_window. coordinate_space is required: image/desktop_physical_px for points, none for keyboard/window/element. Focus_window can recover lost foreground. Use same invocation_id only for identical retries. Partial input releases delivered key-downs and is never replayed; verify actual files after saving.")]
    public Task<CallToolResult> Act(string observation_id, string action, string coordinate_space, string invocation_id,
        int? x = null, int? y = null, int? end_x = null, int? end_y = null, string? element_id = null,
        string? text = null, string? key = null, int wheel_delta = -120, bool observe_after = true) =>
        runtime.Ledger.Invoke(invocation_id, "computer_act", new { observation_id, action, coordinate_space, x, y, end_x, end_y, element_id, text, key, wheel_delta, observe_after },
            "desktop:input", _ => desktop.Act(observation_id, action, coordinate_space, x, y, end_x, end_y, element_id, text, key, wheel_delta, observe_after), 1000);
}
