using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Upton.Pdm.PreviewAgent;

/// <summary>
/// 极简HTTP/1.1 请求解析与响应写入：转图代理只需要 /health 与 /convert 两个接口，
/// 不依赖 http.sys，因此不需要管理员预留URL，也不需要额外组件。
/// </summary>
internal sealed class AgentHttpRequest
{
    private readonly Dictionary<string, string> headers;

    private AgentHttpRequest(string method, string path, Dictionary<string, string> headers, Stream body, long contentLength)
    {
        this.headers = headers;
        Method = method;
        Path = path;
        Body = body;
        ContentLength = contentLength;
    }

    public string Method { get; }

    public string Path { get; }

    public Stream Body { get; }

    public long ContentLength { get; }

    public string Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

    public static async Task<AgentHttpRequest> ReadAsync(Stream stream)
    {
        var requestLine = await ReadLineAsync(stream);
        if (string.IsNullOrWhiteSpace(requestLine)) return null;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) throw new InvalidDataException($"无法解析HTTP请求行：{requestLine}");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (string.IsNullOrEmpty(line)) break;
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            headers[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }
        var contentLength = 0L;
        var lengthText = headers.TryGetValue("Content-Length", out var value) ? value : null;
        if (!string.IsNullOrWhiteSpace(lengthText) && !long.TryParse(lengthText, out contentLength))
            throw new InvalidDataException("Content-Length 无效。");
        return new AgentHttpRequest(parts[0], parts[1], headers, stream, Math.Max(0, contentLength));
    }

    public async Task CopyBodyToAsync(Stream target, long maxBytes)
    {
        if (ContentLength <= 0) throw new InvalidDataException("转换请求没有内容。");
        if (ContentLength > maxBytes) throw new InvalidDataException($"转换请求过大（{ContentLength} 字节），超过上限 {maxBytes} 字节。");
        var remaining = ContentLength;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = await Body.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new InvalidDataException("转换请求内容不完整。");
            await target.WriteAsync(buffer, 0, read);
            remaining -= read;
        }
    }

    public static async Task WriteResponseAsync(Stream stream, int statusCode, string contentType, long contentLength, Stream body)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(StatusText(statusCode)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await body.CopyToAsync(stream, 81920);
        await stream.FlushAsync();
    }

    private static string StatusText(int statusCode) => statusCode switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Internal Server Error"
    };

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var buffer = new List<byte>(128);
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (value == '\n')
            {
                if (buffer.Count > 0 && buffer[buffer.Count - 1] == '\r') buffer.RemoveAt(buffer.Count - 1);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            buffer.Add((byte)value);
            if (buffer.Count > 8192) throw new InvalidDataException("HTTP请求头过大。");
        }
    }
}
