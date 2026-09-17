using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public static class DesktopTests
{
    private static int passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"DESKTOP PASS {++passed:000} {message}");
    }
    private static JsonElement Data(CallToolResult r) => r.StructuredContent!.Value.GetProperty("data");
    private static string Id(CallToolResult r) => Data(r).GetProperty("observation_id").GetString()!;
    private static string PostId(CallToolResult r) => Data(r).GetProperty("observation").GetProperty("data").GetProperty("observation_id").GetString()!;
    private static void Fault(Action action, string code)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (CodexishFault f) { Check(f.Code == code, "geometry refuses invalid input: " + code); }
    }
    private sealed class Fake : IDesktopPlatform
    {
        public DesktopWindow Window = new("test-window", 123, 456, 789, "Fixture", new(-1500, 0, 1200, 700), false, false);
        public DesktopRect Bounds = new(-1920, -200, 3840, 1280);
        public string? Foreground = "test-window";
        public uint Tick;
        public int Sent;
        public List<DesktopInput[]> Batches = [];
        public int? Partial;
        public bool FailCapture;
        public bool FailAfterInput;
        public bool Occluded;
        public bool ElementMoved;
        public DesktopScene Inspect() => new(Bounds, "layout", [Window], Foreground, Tick, -1000, 50);
        public byte[] Capture(DesktopGeometry g)
        {
            if (FailCapture) throw new IOException("Synthetic capture failure");
            // This is explicitly a fake backend. Native capture never falls back to these bytes.
            return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOcQAAAAASUVORK5CYII=");
        }
        public DesktopUi Query(DesktopWindow window) => new([
            new("editor", "Edit", "Fixture editor", new(ElementMoved ? -1300 : -1400, 50, 500, 300), true, false, true, false, false, "fixture"),
            new("tab", "TabItem", "Untitled fixture", new(-1400, 0, 100, 30), false, false, true, false, true, null),
            new("password", "Edit", "Password", new(-1400, 400, 100, 30), false, true, true, false, false, "must-never-leave")]);
        public long HitTest(int x, int y) => Occluded ? 987 : Window.Handle;
        public int Send(DesktopInput[] input, DesktopRect desktop)
        {
            Batches.Add(input); Sent++;
            int delivered = Partial ?? input.Length; Partial = null;
            if (FailAfterInput) FailCapture = true;
            return delivered;
        }
        public bool WindowAction(DesktopWindow window, string action)
        {
            if (action == "focus_window") Foreground = Window.Id;
            return true;
        }
    }
    private static Task<CallToolResult> Act(DesktopService service, string id, string action, string space = "none",
        int? x = null, int? y = null, string? element = null, string? text = null, string? key = null, bool after = true) =>
        service.Act(id, action, space, x, y, null, null, element, text, key, -120, after);
    public static async Task<int> Run()
    {
        try
        {
            var geometry = DesktopGeometry.Create(new(-1920, -200, 3840, 1280), 1280);
            Check(geometry.Width == 1280 && geometry.Height == 427, "negative virtual desktop and rounded image dimensions");
            Check(geometry.Point(640, 0, "image") == (0, -200), "image transform includes negative virtual origin exactly once");
            Check(geometry.Point(-1200, 100, "desktop_physical_px") == (-1200, 100), "explicit physical pixels are not scaled again");
            Check(DesktopGeometry.Create(new(0, 0, 800, 600), 1280).Width == 800, "small images are not upscaled");
            Check(DesktopGeometry.Create(new(0, 0, 3840, 2160), 0).Width == 3840, "zero maximum preserves native resolution");
            Check(DesktopGeometry.Create(new(0, 0, 100, 1), 1).Height == 1, "tiny scaled image height remains positive");
            Fault(() => geometry.Point(1280, 0, "image"), "INVALID_ARGUMENT");
            Fault(() => geometry.Point(0, 427, "image"), "INVALID_ARGUMENT");
            Fault(() => DesktopGeometry.Create(new(0, 0, 1, 1), -1), "INVALID_ARGUMENT");
            var combo = DesktopInputPlan.Combo("CTRL+SHIFT+S");
            var release = DesktopInputPlan.Releases(combo, 3);
            Check(release.Select(e => e.Code).SequenceEqual(new[] { (int)'S', 16, 17 }) && release.All(e => e.Up), "partial chord releases all and only outstanding delivered keys in reverse order");
            Check(DesktopInputPlan.Releases(combo, combo.Length).Length == 0, "completed chord needs no synthetic releases");
            Check(DesktopInputPlan.Releases([new("move"), new("left"), new("right"), new("left", Up: true)], 4).Single().Kind == "right", "partial pointer plan releases only the still-held button");
            var unicode = DesktopInputPlan.Text("한글🙂");
            Check(unicode.Length == 8 && DesktopInputPlan.Releases(unicode, 1).Single() == unicode[0] with { Up = true }, "UTF16 input and partial Unicode cleanup preserve code units");
            Fault(() => DesktopInputPlan.Combo("CTRL+CTRL"), "INVALID_ARGUMENT");
            var fake = new Fake(); using var desktop = new DesktopService(fake);
            var observation = await desktop.Observe(fake.Window.Id);
            Check(Reply.CodeOf(observation) is null && observation.Content.OfType<ImageContentBlock>().Count() == 1, "observation carries image and structured metadata");
            Check(Data(observation).GetProperty("ui").GetProperty("active_tabs")[0].GetString() == "Untitled fixture", "selected tab exposed in observation metadata");
            string id = Id(observation);
            var page = await desktop.Query(id, null, null, null, 1);
            Check(Data(page).GetProperty("total").GetInt32() == 3 && Data(page).GetProperty("next_cursor").ValueKind == JsonValueKind.String, "UIA results page without discarding the full query");
            string cursor = Data(page).GetProperty("next_cursor").GetString()!;
            Check(Reply.CodeOf(await desktop.Query(id, "Edit", null, cursor, 1)) == "CURSOR_INVALID", "UI cursor is bound to its filters");
            var password = await desktop.Query(id, null, "Password", null, 100);
            Check(Data(password).GetProperty("elements")[0].GetProperty("text").ValueKind == JsonValueKind.Null, "password values are absent even when provider returns a value");
            var edit = await desktop.Query(id, "Edit", "Fixture editor", null, 100);
            string element = Data(edit).GetProperty("elements")[0].GetProperty("element_id").GetString()!;
            fake.ElementMoved = true;
            Check(Reply.CodeOf(await Act(desktop, id, "click_element", element: element)) == "STALE_OBSERVATION" && fake.Sent == 0, "moved semantic element refuses input before sending");
            fake.ElementMoved = false; fake.Tick++;
            var click = await Act(desktop, id, "click_element", element: element);
            Check(Reply.CodeOf(click) is null && fake.Sent == 1, "InputTick change alone does not block valid input");
            Check(Data(click).GetProperty("business_outcome").GetString() == "not_verified" && click.Content.OfType<ImageContentBlock>().Count() == 1, "action includes follow-up pixels without claiming business success");
            id = PostId(click); int sent = fake.Sent; fake.Foreground = "other-window";
            Check(Reply.CodeOf(await Act(desktop, id, "type_text", text: "no")) == "STALE_OBSERVATION" && fake.Sent == sent, "foreground loss stops keyboard input");
            var focused = await Act(desktop, id, "focus_window");
            Check(Reply.CodeOf(focused) is null && fake.Foreground == fake.Window.Id, "focus_window recovers foreground using the bound target");
            id = PostId(focused); fake.Window = fake.Window with { StartTicks = 790 };
            Check(Reply.CodeOf(await Act(desktop, id, "type_text", text: "no")) == "STALE_OBSERVATION", "PID/window reuse with changed process start identity is stale");
            fake.Window = fake.Window with { StartTicks = 789 }; fake.Occluded = true;
            Check(Reply.CodeOf(await Act(desktop, id, "click_coordinate", "desktop_physical_px", -1400, 50)) == "STALE_OBSERVATION", "occluding window is never clicked through");
            fake.Occluded = false; fake.Partial = 2;
            var partial = await Act(desktop, id, "key_combo", key: "CTRL+S");
            Check(Reply.CodeOf(partial) == "EXECUTION_UNKNOWN" && partial.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() == "partial", "partial native input reports uncertainty rather than success");
            Check(fake.Batches.Last().Select(i => i.Code).SequenceEqual(new[] { (int)'S', 17 }) && fake.Batches.Last().All(i => i.Up), "partial handler sends key-up cleanup, not the original action");
            fake.FailAfterInput = true;
            var noPost = await Act(desktop, id, "type_text", text: "effect");
            Check(Reply.CodeOf(noPost) == "EXECUTION_UNKNOWN" && noPost.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() != "none", "post-capture failure cannot erase an already-delivered effect");
            fake.FailAfterInput = false; fake.FailCapture = false;
            string state = Path.Combine(Path.GetTempPath(), "codexish-desktop-ledger-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(state);
            try
            {
                using var store = new Store(Path.Combine(state, "test.db")); using var ledger = new Ledger(store);
                string operation = "desktop-dedupe"; int before = fake.Sent;
                var first = await ledger.Invoke(operation, "computer_act", new { text = "once" }, "desktop:input", _ => Act(desktop, id, "type_text", text: "once"), 10000);
                var second = await ledger.Invoke(operation, "computer_act", new { text = "once" }, "desktop:input", _ => throw new InvalidOperationException("replay"), 10000);
                Check(Reply.CodeOf(first) is null && Reply.CodeOf(second) is null && fake.Sent == before + 1, "real ledger stores image-bearing action result and does not replay identical input");
                Check(Reply.CodeOf(await ledger.Invoke(operation, "computer_act", new { text = "other" }, "desktop:input", _ => throw new InvalidOperationException(), 10000)) == "IDEMPOTENCY_CONFLICT", "changed action arguments cannot reuse an invocation");
            }
            finally { Directory.Delete(state, true); }
            if (!OperatingSystem.IsWindows())
            {
                using var unavailable = new DesktopService();
                Check(Reply.CodeOf(await unavailable.Observe()) == "UNSUPPORTED_CAPABILITY", "non-Windows backend explicitly refuses native capture instead of substituting fake pixels");
            }
            Console.WriteLine($"DESKTOP_CORE_PASSED: {passed}; deterministic fake backend, not interactive desktop evidence.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"DESKTOP_CORE_FAILED after {passed}: {e}"); return 1; }
    }
}
