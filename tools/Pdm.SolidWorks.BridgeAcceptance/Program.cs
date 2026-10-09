using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.Desktop;

internal static class Program
{
    private static async Task Main()
    {
        await WaitForExitOrReady();
        await Exchange("accepted", 0, 0, null);
        await Exchange("accepted", 1200, 0, null); // Later than the former 800 ms limit.
        await Exchange("accepted", 400, 0, null, 200); // Server appears after a failed attempt.
        await Exchange("rejected", 0, 0, "拒绝");
        await Exchange(null, 0, 0, "通信中断");
        await Exchange("accepted", 0, 600, "确认超时");
        var unavailable = await SolidWorksPipeSender.SendAsync(Guid.NewGuid().ToString("N"), "{}", CancellationToken.None, 30, 100);
        Check(!unavailable, "Unavailable server exhausts bounded connection retries");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await SolidWorksPipeSender.SendAsync(Guid.NewGuid().ToString("N"), "{}", cancelled.Token); throw new Exception("Cancellation swallowed"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS: Caller cancellation preserved"); }
        }
    }

    private static async Task WaitForExitOrReady()
    {
        var checks = 0;
        var sends = 0;
        var result = await SolidWorksOpenWaiter.WaitAsync(() => ++checks < 4,
            () => { sends++; return Task.FromResult(false); }, CancellationToken.None, 1000, 1);
        Check(result == SolidWorksWaitResult.Exited && sends == 3, "Closing process is awaited until exit");
        sends = 0;
        result = await SolidWorksOpenWaiter.WaitAsync(() => true,
            () => Task.FromResult(++sends == 3), CancellationToken.None, 1000, 1);
        Check(result == SolidWorksWaitResult.Accepted && sends == 3, "Loading plugin receives request once when ready");
        result = await SolidWorksOpenWaiter.WaitAsync(() => true,
            () => Task.FromResult(false), CancellationToken.None, 10, 1);
        Check(result == SolidWorksWaitResult.TimedOut, "Stuck process ends waiting without exception");
        sends = 0;
        try
        {
            await SolidWorksOpenWaiter.WaitAsync(() => true,
                () => { sends++; throw new InvalidOperationException("ambiguous acknowledgement"); }, CancellationToken.None, 1000, 1);
            throw new Exception("Ambiguous send retried");
        }
        catch (InvalidOperationException) { Check(sends == 1, "No retry after ambiguous acknowledgement"); }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await SolidWorksOpenWaiter.WaitAsync(() => true, () => Task.FromResult(false), cancellation.Token);
                throw new Exception("Wait cancellation swallowed");
            }
            catch (OperationCanceledException) { Console.WriteLine("PASS: Wait cancellation preserved"); }
        }
    }

    private static async Task Exchange(string? reply, int startDelay, int replyDelay, string? expectedError, int connectTimeout = 3000)
    {
        var name = "UPLM.BridgeTest." + Guid.NewGuid().ToString("N");
        int received = 0;
        var server = Task.Run(async () =>
        {
            await Task.Delay(startDelay);
            using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var deadline = new CancellationTokenSource(8000))
            using (deadline.Token.Register(pipe.Dispose))
            {
                await pipe.WaitForConnectionAsync();
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                {
                    Check(await reader.ReadLineAsync() == "{\"request\":\"once\"}", "Request body intact");
                    Interlocked.Increment(ref received);
                    await Task.Delay(replyDelay);
                    if (reply != null)
                    {
                        try
                        {
                            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                                await writer.WriteLineAsync(reply);
                        }
                        catch (IOException) when (replyDelay > 300) { }
                    }
                }
            }
        });
        try
        {
            var accepted = await SolidWorksPipeSender.SendAsync(name, "{\"request\":\"once\"}", CancellationToken.None, connectTimeout, 300);
            Check(expectedError == null && accepted, "Accepted after connection delay " + startDelay);
        }
        catch (InvalidOperationException error) when (expectedError != null)
        {
            Check(error.Message.Contains(expectedError), "Correct failure: " + expectedError);
        }
        await server;
        Check(received == 1, "Request sent exactly once");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
