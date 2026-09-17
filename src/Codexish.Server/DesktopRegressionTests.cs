using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Regression checks deliberately use a fake platform and never send native input.
public static class DesktopRegressionTests
{
    private sealed class Platform : IDesktopPlatform
    {
        public DesktopWindow Window = new("window", 10, 20, 30, "Fixture", new(100, 100, 600, 400), false, false);
        public DesktopRect Bounds = new(0, 0, 2400, 1600);
        public int ElementX = 120, Sent, Captures;
        public DesktopGeometry? LastCapture;
        public DesktopScene Inspect() => new(Bounds, "layout", [Window], Window.Id, 0, 0, 0);
        public byte[] Capture(DesktopGeometry geometry)
        {
            LastCapture = geometry; Captures++;
            return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOcQAAAAASUVORK5CYII=");
        }
        public DesktopUi Query(DesktopWindow window) => new([
            new("a", "Edit", "Editor", new(ElementX, 130, 200, 70), true, false, true, false, false, "text"),
            new("b", "Button", "Save", new(140, 220, 100, 30), false, false, true, false, false, null),
            new("c", "Button", "Cancel", new(140, 280, 100, 30), false, false, true, false, false, null),
            new("d", "TabItem", "Untitled", new(140, 320, 100, 30), false, false, true, false, true, null)]);
        public long HitTest(int x, int y) => Window.Handle;
        public int Send(DesktopInput[] inputs, DesktopRect desktop) { Sent++; return inputs.Length; }
        public bool WindowAction(DesktopWindow window, string action)
        {
            Window = action switch
            {
                "maximize_window" => Window with { Bounds = new(0, 0, 2000, 1400), Minimized = false },
                "minimize_window" => Window with { Bounds = new(-32000, -32000, 160, 28), Minimized = true },
                "restore_window" => Window with { Bounds = new(100, 100, 600, 400), Minimized = false },
                _ => Window
            };
            return true;
        }
    }
    private static JsonElement Data(CallToolResult result) => result.StructuredContent!.Value.GetProperty("data");
    private static string Id(CallToolResult result) => Data(result).GetProperty("observation_id").GetString()!;
    private static Task<CallToolResult> Act(DesktopService desktop, string id, string action, string? element = null) =>
        desktop.Act(id, action, "none", null, null, null, null, element, null, null, 0, true);
    private static int passed, failed;
    private static void Check(bool condition, string name)
    {
        if (condition) { passed++; Console.WriteLine("DESKTOP REGRESSION PASS " + name); }
        else { failed++; Console.Error.WriteLine("DESKTOP REGRESSION FAIL " + name); }
    }
    public static async Task<int> Run()
    {
        passed = failed = 0;
        try
        {
            var platform = new Platform(); using var desktop = new DesktopService(platform);
            string id = Id(await desktop.Observe(platform.Window.Id));
            var first = await desktop.Query(id, null, null, null, 1);
            string cursor = Data(first).GetProperty("next_cursor").GetString()!;
            var page = await desktop.Query(id, null, null, cursor, 1);
            var replay = await desktop.Query(id, null, null, cursor, 1);
            Check(page.StructuredContent!.Value.GetRawText() == replay.StructuredContent!.Value.GetRawText(), "S2-01 identical cursor reproduces the complete response and next cursor");
            Check(Reply.CodeOf(await desktop.Query(id, null, null, cursor, 2)) == "CURSOR_INVALID", "S2-01 page size is bound to the cursor");
            var ids = new List<string> { Data(first).GetProperty("elements")[0].GetProperty("element_id").GetString()! };
            for (var current = page; ; )
            {
                ids.AddRange(Data(current).GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("element_id").GetString()!));
                var next = Data(current).GetProperty("next_cursor");
                if (next.ValueKind == JsonValueKind.Null) break;
                current = await desktop.Query(id, null, null, next.GetString(), 1);
            }
            Check(ids.Count == 4 && ids.Distinct().Count() == 4, "S2-01 traversal has neither gaps nor duplicates");
            var oldQuery = await desktop.Query(id, "Edit", "Editor", null, 1);
            string oldElement = Data(oldQuery).GetProperty("elements")[0].GetProperty("element_id").GetString()!;
            platform.ElementX += 20;
            var newQuery = await desktop.Query(id, "Edit", "Editor", null, 1);
            string newElement = Data(newQuery).GetProperty("elements")[0].GetProperty("element_id").GetString()!;
            Check(oldElement != newElement, "S2-02 a changed snapshot never rebinds an issued element reference");
            int before = platform.Sent;
            var oldAction = await Act(desktop, id, "click_element", oldElement);
            Check(Reply.CodeOf(oldAction) == "STALE_OBSERVATION" && platform.Sent == before, "S2-02 original reference refuses the moved element after re-query");
            Check(Reply.CodeOf(await Act(desktop, id, "click_element", newElement)) is null, "S2-02 fresh reference resolves its own current snapshot");
            id = Id(await desktop.Observe(platform.Window.Id, 0, true));
            var maximized = await Act(desktop, id, "maximize_window");
            Check(Reply.CodeOf(maximized) is null && platform.LastCapture?.Width == 2000, "S2-03 native width survives window resize");
            platform.WindowAction(platform.Window, "restore_window");
            id = Id(await desktop.Observe(platform.Window.Id, 1280, true));
            var scaled = await Act(desktop, id, "maximize_window");
            Check(Reply.CodeOf(scaled) is null && platform.LastCapture?.Width == 1280, "S2-03 the requested maximum is retained instead of the former small image width");
            id = Id(await desktop.Observe(platform.Window.Id, 640, true));
            int captured = platform.Captures;
            var minimized = await Act(desktop, id, "minimize_window");
            bool ok = Reply.CodeOf(minimized) is null;
            Check(ok && platform.Captures == captured && !minimized.Content.OfType<ImageContentBlock>().Any(), "S2-03 minimizing a window-only view returns metadata without capturing unrelated pixels");
            if (ok)
            {
                var post = Data(minimized).GetProperty("observation").GetProperty("data");
                Check(!post.GetProperty("image_available").GetBoolean(), "S2-03 missing image is explicit");
                var restored = await Act(desktop, post.GetProperty("observation_id").GetString()!, "restore_window");
                Check(Reply.CodeOf(restored) is null && restored.Content.OfType<ImageContentBlock>().Count() == 1, "S2-03 minimized metadata supports restore and a fresh image");
            }
            Console.WriteLine($"DESKTOP_REGRESSIONS: {passed} passed; {failed} failed; fake platform only.");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
