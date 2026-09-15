using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.P0.V1;

// Deterministic synchronization for acceptance/cancellation tests; no user files or GUI.
public static class FifoRegressionTests
{
    public static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine($"FIFO PASS {++passed:00} {name}");
        }
        string directory = Path.Combine(Path.GetTempPath(), "codexish-fifo-" + Guid.NewGuid().ToString("N"));
        Store? store = null; Operations? operations = null;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            store = new Store(directory); operations = new Operations(store);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<int>();
            Task<CallToolResult> first = operations.Invoke("s", "first", "fixture", new { n = 1 }, ["shared"], async ct =>
            {
                lock (order) order.Add(1);
                entered.TrySetResult(true); await release.Task.WaitAsync(ct); return VReply.Ok(new { });
            }, 0);
            await first; await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await operations.Invoke("s", "middle", "fixture", new { n = 2 }, ["shared"], _ => throw new InvalidOperationException("cancelled effect ran"), 0);
            operations.Cancel("s", "middle");
            var cancelled = await operations.Invoke("s", "middle", "fixture", new { n = 2 }, ["shared"], _ => throw new InvalidOperationException("replayed"), 10000);
            Check(VReply.Status(cancelled) == "cancelled" && !release.Task.IsCompleted, "queued cancellation returns before the running predecessor finishes");
            await operations.Invoke("s", "third", "fixture", new { n = 3 }, ["shared"], _ =>
            {
                lock (order) order.Add(3);
                thirdEntered.TrySetResult(true); return Task.FromResult(VReply.Ok(new { }));
            }, 0);
            Task independent = operations.Invoke("s", "independent", "fixture", new { }, ["other"], _ => Task.FromResult(VReply.Ok(new { })), 10000);
            await independent;
            await Task.Delay(100); // Give an erroneously released successor a chance to enter.
            Check(!thirdEntered.Task.IsCompleted && VReply.Status(operations.Inspect("s", "third")) == "queued", "cancelled middle item retains its predecessor resource barrier");
            release.TrySetResult(true);
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completed = await operations.Invoke("s", "third", "fixture", new { n = 3 }, ["shared"], _ => throw new InvalidOperationException("replayed"), 10000);
            lock (order) Check(order.SequenceEqual([1, 3]) && VReply.Status(completed) == "succeeded", "successor runs after predecessor and never runs the cancelled effect");
            var missing = await operations.Invoke("s", "cancel-missing", "operation.cancel", new { target = "missing" }, ["cancel:missing"], _ => Task.FromResult(operations.Cancel("s", "missing")), 10000);
            Check(missing.IsError == true && missing.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "NOT_FOUND", "unknown cancellation target is an error rather than a false acknowledgement");
            await operations.Close(); store.Dispose(); store = null;
            Directory.Delete(directory, true);
            string? evidence = Environment.GetEnvironmentVariable("CODEXISH_TEST_OUTPUT");
            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "fifo-regression-results.json"), JsonSerializer.Serialize(new { passed, cleanup_complete = true }));
            }
            Console.WriteLine($"FIFO_REGRESSION_PASSED: {passed}"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FIFO_REGRESSION_FAILED after {passed}: {e}"); return 1; }
        finally
        {
            release.TrySetResult(true);
            if (store is not null)
            {
                if (operations is not null) await operations.Close();
                store.Dispose();
            }
            if (Directory.Exists(directory))
                try { Directory.Delete(directory, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("fifo_fixture_cleanup_failed: " + e.GetType().Name); }
        }
    }
}
