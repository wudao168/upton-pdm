using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

internal static partial class Program
{
    // --preview-download-benchmark <desktop.exe> <secrets.json> <result.json> <base URL> <document ID>
    private static async Task<int> RunPreviewDownloadBenchmark(string[] args)
    {
        var desktop = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = Path.Combine(Path.GetDirectoryName(desktop), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var type = Assembly.LoadFrom(desktop).GetType("Upton.Pdm.ClientShared.ClientDiagnosticHandler", true);
        var transport = (HttpMessageHandler)Activator.CreateInstance(type, new object[] { new HttpClientHandler { MaxConnectionsPerServer = 8 } });
        using (var client = new HttpClient(transport) { BaseAddress = new Uri(args[4]), Timeout = TimeSpan.FromMinutes(2) })
        {
            var secrets = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[2]));
            var login = await client.PostAsync("/api/auth/login", new StringContent(Json.Serialize(new { username = secrets["bootstrapAdminUsername"], password = secrets["bootstrapAdminPassword"] }), System.Text.Encoding.UTF8, "application/json"));
            login.EnsureSuccessStatusCode();
            var session = Json.Deserialize<Dictionary<string, object>>(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)session["accessToken"]);
            try
            {
                var metadata = await client.GetAsync("/api/documents/" + args[5] + "/preview-source-version");
                metadata.EnsureSuccessStatusCode();
                var version = Json.Deserialize<Dictionary<string, object>>(await metadata.Content.ReadAsStringAsync());
                var length = Convert.ToInt32(version["fileLength"]);
                var url = "/api/documents/" + args[5] + "/versions/" + version["id"] + "/file?download=false";
                var samples = new List<object>();
                foreach (var count in new[] { 1, 4, 4, 1 })
                {
                    var clock = Stopwatch.StartNew();
                    var result = new byte[length];
                    await Task.WhenAll(Enumerable.Range(0, count).Select(async index => {
                        var from = (long)length * index / count;
                        var to = (long)length * (index + 1) / count - 1;
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (count > 1) request.Headers.Range = new RangeHeaderValue(from, to);
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                            {
                                response.EnsureSuccessStatusCode();
                                if (count > 1 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != from
                                    || response.Content.Headers.ContentRange?.To != to || response.Content.Headers.ContentRange?.Length != length))
                                    throw new Exception("服务器分段响应不符合预期。");
                                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                if (bytes.LongLength != to - from + 1) throw new Exception("分段长度不完整。");
                                Buffer.BlockCopy(bytes, 0, result, (int)from, bytes.Length);
                            }
                        }
                    }));
                    var networkMs = clock.ElapsedMilliseconds;
                    using (var hash = SHA256.Create())
                        if (BitConverter.ToString(hash.ComputeHash(result)).Replace("-", "") != version["sha256"].ToString().ToUpperInvariant())
                            throw new Exception("完整下载内容校验失败。");
                    samples.Add(new { requests = count, networkMs, totalMs = clock.ElapsedMilliseconds });
                    Console.WriteLine("下载对照：连接=" + count + "，网络=" + networkMs + "毫秒");
                }
                File.WriteAllText(args[3], Json.Serialize(new { status = "passed", length, samples }));
            }
            finally { await client.PostAsync("/api/auth/logout", new StringContent("")); }
        }
        return 0;
    }
}
