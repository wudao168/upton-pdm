using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class SolidWorksOpenRequest
{
    public Guid ProjectId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? VersionId { get; set; }
    public string Mode { get; set; }
}

internal sealed class SolidWorksOpenRequestListener : IDisposable
{
    private readonly ConcurrentQueue<SolidWorksOpenRequest> requests = new ConcurrentQueue<SolidWorksOpenRequest>();
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private Task listenerTask;

    public void Start()
    {
        TryLoadPendingRequest();
        listenerTask = Task.Run(() => ListenAsync(cancellation.Token));
    }

    public bool TryDequeue(out SolidWorksOpenRequest request)
    {
        TryLoadPendingRequest();
        return requests.TryDequeue(out request);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { listenerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using (var pipe = new NamedPipeServerStream(
                    PipeName(),
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous))
                using (token.Register(pipe.Dispose))
                {
                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    {
                        var json = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(json) && json.Length <= 32 * 1024)
                        {
                            var request = serializer.Deserialize<SolidWorksOpenRequest>(json);
                            if (IsValid(request))
                            {
                                requests.Enqueue(request);
                                await writer.WriteLineAsync("accepted").ConfigureAwait(false);
                                continue;
                            }
                        }

                        await writer.WriteLineAsync("rejected").ConfigureAwait(false);
                    }
                }
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
            catch (IOException) when (token.IsCancellationRequested) { }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }
    }

    private void TryLoadPendingRequest()
    {
        var path = PendingRequestPath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var request = serializer.Deserialize<SolidWorksOpenRequest>(json);
            if (IsValid(request))
            {
                requests.Enqueue(request);
            }
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException)
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static bool IsValid(SolidWorksOpenRequest request) =>
        request != null
        && request.ProjectId != Guid.Empty
        && request.DocumentId != Guid.Empty
        && (string.Equals(request.Mode, "LatestReadOnly", StringComparison.Ordinal)
            || string.Equals(request.Mode, "LatestReleased", StringComparison.Ordinal)
            || string.Equals(request.Mode, "LatestEdit", StringComparison.Ordinal)
            || string.Equals(request.Mode, "SpecificReadOnly", StringComparison.Ordinal) && request.VersionId.HasValue);

    internal static string PipeName()
    {
        var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? Environment.UserName;
        return string.Concat("UPTON.PDM.SolidWorks.", sid.Replace('-', '_').Replace('\\', '_'));
    }

    internal static string PendingRequestPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "solidworks-open-request.json");
}
