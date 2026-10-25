using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.PreviewAgent;

internal sealed class AgentBatchContext
{
    public string Initiator { get; set; }
    public string ProjectCode { get; set; }
    public string Source { get; set; }
    public string RootFileName { get; set; }
    public string RequestId { get; set; }
}

internal sealed class AgentLog
{
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; }
    public string Message { get; set; }
    public Guid? BatchId { get; set; }
}

internal sealed class AgentFileRecord
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; }
    public string Status { get; set; } = "待处理";
    public string Message { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
}

internal sealed class AgentBatch
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = "排队中";
    public string Stage { get; set; } = "等待空闲转换程序";
    public string RemoteAddress { get; set; }
    public AgentBatchContext Context { get; set; } = new AgentBatchContext();
    public List<AgentFileRecord> Files { get; set; } = new List<AgentFileRecord>();
    public List<AgentLog> Logs { get; set; } = new List<AgentLog>();
    public int ReferenceFiles { get; set; }
    public int Completed { get; set; }
    public string Error { get; set; }
    public bool Returned { get; set; }
    public int SuccessCount => Files.Count(file => file.Status == "成功");
    public int FailedCount => Files.Count(file => file.Status == "失败");
}

internal sealed class AgentJournal
{
    private readonly object gate = new object();
    private readonly string directory;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private readonly List<AgentBatch> batches = new List<AgentBatch>();
    private readonly List<AgentLog> logs = new List<AgentLog>();
    public long TotalRequests { get; private set; }
    public string StorageError { get; private set; }

    public AgentJournal(string directory, int retentionDays)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        var sequence = Path.Combine(directory, "sequence.txt");
        if (File.Exists(sequence) && long.TryParse(File.ReadAllText(sequence), out var number)) TotalRequests = number;
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var batch = json.Deserialize<AgentBatch>(File.ReadAllText(path, Encoding.UTF8));
                if (batch == null) continue;
                batches.Add(batch);
                TotalRequests = Math.Max(TotalRequests, batch.Number);
                if (!batch.FinishedUtc.HasValue)
                    Complete(batch.Id, false, "代理曾退出或重新启动，本批次未完成，请重新发起。");
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException)
            {
                StorageError = "部分处理记录无法读取，请检查记录目录；其他记录仍可查看。";
                Diagnostic(error.ToString());
            }
        }
        Prune(retentionDays);
    }

    public AgentBatch Begin(string remoteAddress)
    {
        lock (gate)
        {
            var batch = new AgentBatch { Id = Guid.NewGuid(), Number = ++TotalRequests, RemoteAddress = remoteAddress };
            batches.Add(batch);
            Write(Path.Combine(directory, "sequence.txt"), TotalRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Log("信息", "收到转图请求，批次 #" + batch.Number.ToString("D4"), batch.Id);
            return batch;
        }
    }

    public void Update(Guid id, Action<AgentBatch> update)
    {
        lock (gate)
        {
            var batch = batches.Single(item => item.Id == id);
            update(batch);
            Save(batch);
        }
    }

    public void Progress(Guid id, string stage, int completed)
    {
        lock (gate)
        {
            var batch = batches.Single(item => item.Id == id);
            batch.Stage = AgentText.Safe(stage, "正在处理图档，请稍候。");
            batch.Completed = Math.Min(batch.Files.Count, Math.Max(0, completed));
            foreach (var file in batch.Files.Where(file => !string.IsNullOrWhiteSpace(file.FileName) && stage?.Contains(file.FileName) == true))
            {
                if (!file.StartedUtc.HasValue) { file.StartedUtc = DateTime.UtcNow; file.Status = "处理中"; }
                if (stage.StartsWith("已处理文件", StringComparison.Ordinal)) file.FinishedUtc = DateTime.UtcNow;
            }
            Log("信息", batch.Stage, id);
        }
    }

    public void Complete(Guid id, bool returned, string error = null)
    {
        lock (gate)
        {
            var batch = batches.Single(item => item.Id == id);
            batch.Returned = returned;
            batch.FinishedUtc = DateTime.UtcNow;
            batch.Error = error;
            foreach (var file in batch.Files.Where(file => file.Status != "成功" && file.Status != "失败"))
            {
                file.Status = "失败";
                file.Message = error ?? "未收到该文件的完整处理结果，请重新发起。";
            }
            batch.Completed = batch.Files.Count;
            batch.Status = error != null || batch.SuccessCount == 0 ? "失败" : batch.FailedCount == 0 ? "成功" : "部分成功";
            batch.Stage = error ?? (batch.Status == "成功" ? "处理完成，结果已返回服务器" : "处理结束，部分图档需要重试");
            Log(batch.Status == "成功" ? "成功" : "提醒", batch.Stage, id);
        }
    }

    public void Log(string level, string message, Guid? batchId = null)
    {
        lock (gate)
        {
            var entry = new AgentLog { Level = level, Message = ClientLogRedaction.Clean(message), BatchId = batchId };
            logs.Add(entry);
            if (logs.Count > 1000) logs.RemoveAt(0);
            if (batchId.HasValue)
            {
                var batch = batches.Single(item => item.Id == batchId.Value);
                batch.Logs.Add(entry);
                if (batch.Logs.Count > 1000) batch.Logs.RemoveAt(0);
                Save(batch);
            }
        }
    }

    public List<AgentBatch> Snapshot()
    {
        lock (gate) return batches.OrderByDescending(item => item.Number).Select(item => new AgentBatch
        {
            Id = item.Id, Number = item.Number, ReceivedUtc = item.ReceivedUtc, StartedUtc = item.StartedUtc, FinishedUtc = item.FinishedUtc,
            Status = item.Status, Stage = item.Stage, RemoteAddress = item.RemoteAddress, Context = item.Context, ReferenceFiles = item.ReferenceFiles,
            Completed = item.Completed, Error = item.Error, Returned = item.Returned,
            Files = item.Files.Select(file => new AgentFileRecord { DocumentId = file.DocumentId, FileName = file.FileName, Status = file.Status,
                Message = file.Message, StartedUtc = file.StartedUtc, FinishedUtc = file.FinishedUtc }).ToList()
        }).ToList();
    }

    public AgentBatch ReadBatch(Guid id) { lock (gate) return json.Deserialize<AgentBatch>(json.Serialize(batches.Single(item => item.Id == id))); }

    public List<AgentLog> RecentLogs() { lock (gate) return logs.ToList(); }

    public void Diagnostic(string message)
    {
        lock (gate)
        {
            try { File.AppendAllText(Path.Combine(directory, "diagnostics-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ClientLogRedaction.Clean(message) + Environment.NewLine, Encoding.UTF8); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { StorageError = "处理记录或诊断日志无法写入，请检查目录权限和磁盘空间。"; }
        }
    }

    public void Prune(int days)
    {
        lock (gate)
        {
            foreach (var batch in batches.Where(item => item.FinishedUtc.HasValue && item.ReceivedUtc < DateTime.UtcNow.AddDays(-days)).ToArray())
            {
                var path = Path.Combine(directory, batch.Id.ToString("N") + ".json");
                try { if (File.Exists(path)) File.Delete(path); batches.Remove(batch); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Diagnostic(error.ToString()); }
            }
            foreach (var path in Directory.GetFiles(directory, "diagnostics-*.log"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path).Substring("diagnostics-".Length), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) && date < DateTime.UtcNow.Date.AddDays(-days))
                    try { File.Delete(path); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Diagnostic(error.ToString()); }
        }
    }

    private void Save(AgentBatch batch) => Write(Path.Combine(directory, batch.Id.ToString("N") + ".json"), json.Serialize(batch));

    private void Write(string path, string text)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            StorageError = "处理记录无法保存，请检查记录目录权限和磁盘空间；当前任务仍在处理。";
            Diagnostic(error.ToString());
        }
    }
}

internal static class AgentText
{
    public static string Safe(string message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message) || !message.Any(value => value >= '\u4e00' && value <= '\u9fff')
            || message.Contains("Exception") || message.Contains("System.") || message.Contains("\n   ")) return fallback;
        return ClientLogRedaction.Clean(message).Replace("\r", " ").Replace("\n", " ");
    }
}
