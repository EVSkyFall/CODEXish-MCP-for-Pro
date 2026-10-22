using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
#if WINDOWS
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
#endif

namespace Codexish.Server;

// Explicit test mode, not part of --self-test or the server's grant model.
// It creates and controls only its own new fixture process and never opens an existing user document.
public static class DesktopLiveTests
{
    public static int Fixture(string directory)
    {
#if WINDOWS
        int code = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new TextBox { Text = "fixture-" + Guid.NewGuid().ToString("N")[..8], AcceptsReturn = true, FontSize = 20, Margin = new Thickness(12) };
                AutomationProperties.SetName(editor, "CODEXish fixture editor");
                AutomationProperties.SetAutomationId(editor, "fixture-editor");
                var save = new Button { Content = "Save fixture", Height = 40, Margin = new Thickness(12) };
                AutomationProperties.SetAutomationId(save, "fixture-save");
                var panel = new DockPanel(); DockPanel.SetDock(save, Dock.Bottom); panel.Children.Add(save); panel.Children.Add(editor);
                var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Untitled fixture", Content = panel }); tabs.SelectedIndex = 0;
                var window = new Window { Title = "CODEXish owned desktop test", Width = 720, Height = 480, Left = 100, Top = 100, Content = tabs, Background = System.Windows.Media.Brushes.White };
                void Save() => File.WriteAllText(Path.Combine(directory, "saved.txt"), editor.Text, new UTF8Encoding(false));
                save.Click += (_, _) => Save();
                var command = new RoutedCommand(); window.CommandBindings.Add(new CommandBinding(command, (_, _) => Save()));
                window.InputBindings.Add(new KeyBinding(command, Key.S, ModifierKeys.Control));
                window.ContentRendered += (_, _) =>
                {
                    editor.Focus();
                    File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, handle = (long)new WindowInteropHelper(window).Handle }));
                };
                new Application().Run(window);
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); code = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return code;
#else
        Console.Error.WriteLine("Desktop fixture requires the Windows build."); return 2;
#endif
    }
    private static JsonElement Data(CallToolResult r) => r.StructuredContent!.Value.GetProperty("data");
    private static string Id(CallToolResult r) => Data(r).GetProperty("observation_id").GetString()!;
    private static string PostId(CallToolResult r) => Data(r).GetProperty("observation").GetProperty("data").GetProperty("observation_id").GetString()!;
    private static void Success(CallToolResult result, string step)
    {
        if (Reply.CodeOf(result) is { } error) throw new InvalidOperationException(step + ": " + error + " " + result.StructuredContent);
    }
    public static async Task<int> Run()
    {
#if WINDOWS
        string directory = Path.Combine(Path.GetTempPath(), "codexish-owned-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); Process? fixture = null;
        try
        {
            string exe = Environment.ProcessPath!;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--desktop-fixture"); start.ArgumentList.Add(directory);
            fixture = Process.Start(start)!;
            for (int i = 0; i < 200 && !File.Exists(Path.Combine(directory, "ready.json")) && !fixture.HasExited; i++) await Task.Delay(100);
            if (!File.Exists(Path.Combine(directory, "ready.json"))) throw new InvalidOperationException("Owned fixture did not become ready.");
            var native = new WindowsDesktopPlatform(fixture.Id); using var desktop = new DesktopService(native);
            var target = await desktop.OnThread(() => native.Inspect().Windows.Single(w => w.Pid == fixture.Id));
            var observation = await desktop.Observe(target.Id, 640, true); Success(observation, "capture");
            if (observation.Content.OfType<ImageContentBlock>().Count() != 1) throw new InvalidOperationException("Native PNG missing.");
                        var image = observation.Content.OfType<ImageContentBlock>().Single();
            byte[] png = image.DecodedData.ToArray();
            int imageWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int imageHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            var dimensions = Data(observation).GetProperty("capture").GetProperty("image");
            if (dimensions.GetProperty("width").GetInt32() != imageWidth || dimensions.GetProperty("height").GetInt32() != imageHeight)
                throw new InvalidOperationException($"PNG dimensions differ: actual {imageWidth}x{imageHeight}, metadata {dimensions}, prefix {Convert.ToHexString(png.AsSpan(0, 8))}");
                        using (var stream = new MemoryStream(png))
            using (var bitmap = new System.Drawing.Bitmap(stream))
            {
                var colors = new HashSet<int>();
                for (int row = 0; row < bitmap.Height; row += 7)
                    for (int col = 0; col < bitmap.Width; col += 7)
                    {
                        var pixel = bitmap.GetPixel(col, row);
                        if (pixel.A != 255) throw new InvalidOperationException("Capture unexpectedly contains transparent pixels.");
                        colors.Add(pixel.ToArgb());
                    }
                if (colors.Count < 4) throw new InvalidOperationException("Capture is blank or flat-colored.");
            }
            Console.WriteLine($"LIVE PASS 01 opaque non-flat own-window PNG {imageWidth}x{imageHeight} with matching coordinates");
            var tabs = Data(observation).GetProperty("ui").GetProperty("active_tabs");
            if (!tabs.EnumerateArray().Any(t => t.GetString() == "Untitled fixture")) throw new InvalidOperationException("Active tab metadata missing.");
            Console.WriteLine("LIVE PASS 02 UIA active tab title");
            string id = Id(observation);
            var focused = await desktop.Act(id, "focus_window", "none", null, null, null, null, null, null, null, 0, true);
            Success(focused, "focus"); id = PostId(focused);
            // ShowWindowAsync returns before the compositor finishes its animation. Re-observe; never repeat the effect.
            async Task<string> Settled()
            {
                string? previous = null; int same = 0;
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(100);
                    var current = await desktop.Observe(target.Id, 640, true); Success(current, "settling observation");
                    var data = Data(current);
                    string bounds = data.GetProperty("capture").GetProperty("source").GetRawText();
                    same = data.GetProperty("stable_during_capture").GetBoolean() && bounds == previous ? same + 1 : 0;
                    if (same >= 2) return Id(current);
                    previous = bounds;
                }
                throw new InvalidOperationException("Test window did not settle; no additional input was attempted.");
            }
            id = await Settled();
            var maximized = await desktop.Act(id, "maximize_window", "none", null, null, null, null, null, null, null, 0, true);
            Success(maximized, "maximize"); id = await Settled();
            var restored = await desktop.Act(id, "restore_window", "none", null, null, null, null, null, null, null, 0, true);
            Success(restored, "restore"); id = await Settled();
            Console.WriteLine("LIVE PASS 06 maximize and restore with new post-action transforms");
            var query = await desktop.Query(id, "Edit", "CODEXish fixture editor", null, 100); Success(query, "UIA query");
            string element = Data(query).GetProperty("elements")[0].GetProperty("element_id").GetString()!;
            Console.WriteLine("LIVE PASS 03 UIA semantic editor lookup");
            var click = await desktop.Act(id, "click_element", "none", null, null, null, null, element, null, null, 0, true);
            Success(click, "semantic click"); id = PostId(click);
            var select = await desktop.Act(id, "key_combo", "none", null, null, null, null, null, null, "CTRL+A", 0, true);
            Success(select, "select"); id = PostId(select);
            const string expected = "CODEXish desktop verification - 한글";
            var typed = await desktop.Act(id, "type_text", "none", null, null, null, null, null, expected, null, 0, true);
            Success(typed, "type"); id = PostId(typed);
            Console.WriteLine("LIVE PASS 04 native semantic click and Unicode input with post-observations");
            var saved = await desktop.Act(id, "key_combo", "none", null, null, null, null, null, null, "CTRL+S", 0, true);
            Success(saved, "save");
            string output = Path.Combine(directory, "saved.txt");
            for (int i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(100);
            byte[] actual = await File.ReadAllBytesAsync(output);
            if (!actual.SequenceEqual(Encoding.UTF8.GetBytes(expected))) throw new InvalidOperationException("Saved bytes do not match Unicode input.");
            Console.WriteLine($"LIVE PASS 05 actual saved UTF-8 bytes verified after CTRL+S: {actual.Length} bytes; SHA256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual))}");
            Console.WriteLine("DESKTOP_LIVE_PASSED: 6; self-owned WPF window only. Not Notepad Save As or ChatGPT/Pro measurements.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("DESKTOP_LIVE_FAILED: " + e); return 1; }
        finally
        {
            if (fixture is not null)
            {
                if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(5000)) fixture.Kill(); }
                fixture.Dispose();
            }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
#else
        Console.Error.WriteLine("DESKTOP_LIVE_NOT_RUN: Windows interactive desktop required."); return 2;
#endif
    }
}
