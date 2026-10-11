using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WinCron.Core;

/// <summary>
/// HTTP RPC（SPEC §12，agent 调试通道）：TcpListener 实现的最小 HTTP/1.1，
/// 不用 HttpListener/http.sys（避免 URLACL 权限问题）。只绑定 127.0.0.1。
/// 端点：GET /health、POST /rpc（与命名管道同一命令分发器、同一 JSON 信封）。
/// 单请求不记日志（防刷屏），仅处理异常记 ERROR；服务绝不因单请求崩溃。
/// </summary>
public sealed class HttpRpcServer
{
    private const int MaxHeadBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    private readonly Func<IpcRequest, IpcResponse> _handler;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _loop;

    public HttpRpcServer(Func<IpcRequest, IpcResponse> handler, int port)
    {
        _handler = handler;
        _port = port;
    }

    public int Port => _port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        AppLog.Info($"http rpc listening on 127.0.0.1:{_port}");
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error($"http rpc accept failed: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            var c = client;
            _ = Task.Run(() => HandleClientAsync(c, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                // ---- 读请求头（前 16KB 内必须出现 \r\n\r\n）----
                var head = new MemoryStream();
                var buf = new byte[4096];
                var headEnd = -1;
                while (headEnd < 0)
                {
                    var n = await stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (n <= 0)
                    {
                        return; // 未发完整请求即断开（扫描/探测噪音）：不记日志
                    }

                    head.Write(buf, 0, n);
                    headEnd = IndexOfHeadEnd(head);
                    if (headEnd < 0 && head.Length > MaxHeadBytes)
                    {
                        await WriteJsonAsync(stream, 400, Serialize(IpcResponse.Fail("http head too large")), ct);
                        return;
                    }
                }

                var headBytes = head.ToArray();
                var headText = Encoding.ASCII.GetString(headBytes, 0, headEnd);
                var bodyAlreadyRead = headBytes.Length - (headEnd + 4);

                // ---- 解析请求行与 Content-Length ----
                var lines = headText.Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 400, Serialize(IpcResponse.Fail("malformed request line")), ct);
                    return;
                }

                var method = parts[0].ToUpperInvariant();
                var path = parts[1];
                var contentLength = 0;
                for (var i = 1; i < lines.Length; i++)
                {
                    var idx = lines[i].IndexOf(':');
                    if (idx <= 0)
                    {
                        continue;
                    }

                    if (lines[i][..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(lines[i][(idx + 1)..].Trim(), out contentLength);
                    }
                }

                if (contentLength < 0)
                {
                    await WriteJsonAsync(stream, 400, Serialize(IpcResponse.Fail("invalid Content-Length")), ct);
                    return;
                }

                // body 上限 64KB：超限丢弃已声明 body（封顶 1MB）后回 400，避免客户端写阻塞
                if (contentLength > MaxBodyBytes)
                {
                    var discard = Math.Min(contentLength, 1024 * 1024);
                    var scratch = new byte[8192];
                    while (discard > 0)
                    {
                        var n = await stream.ReadAsync(scratch, 0, Math.Min(scratch.Length, discard), ct);
                        if (n <= 0)
                        {
                            break;
                        }

                        discard -= n;
                    }

                    await WriteJsonAsync(stream, 400,
                        Serialize(IpcResponse.Fail($"request body too large (limit {MaxBodyBytes} bytes)")), ct);
                    return;
                }

                // ---- 读 body（请求头阶段可能已带入一部分）----
                var body = new byte[contentLength];
                var copied = Math.Min(bodyAlreadyRead, contentLength);
                if (copied > 0)
                {
                    Buffer.BlockCopy(headBytes, headEnd + 4, body, 0, copied);
                }

                while (copied < contentLength)
                {
                    var n = await stream.ReadAsync(body, copied, contentLength - copied, ct);
                    if (n <= 0)
                    {
                        return; // body 未到齐即断开：不记日志
                    }

                    copied += n;
                }

                // ---- 分发（方法/路径不匹配 → 400；单请求异常 → 500，均带 JSON 信封）----
                if (method == "GET" && path == "/health")
                {
                    var data = new { pid = Environment.ProcessId, version = GetVersion() };
                    await WriteJsonAsync(stream, 200, Serialize(new { ok = true, data }), ct);
                    return;
                }

                if (method == "POST" && path == "/rpc")
                {
                    string payload;
                    var status = 200;
                    try
                    {
                        IpcRequest req;
                        try
                        {
                            req = JsonSerializer.Deserialize<IpcRequest>(Encoding.UTF8.GetString(body), Json.Default)
                                  ?? new IpcRequest();
                        }
                        catch (JsonException ex)
                        {
                            throw new RpcBadRequestException($"invalid json: {ex.Message}");
                        }

                        payload = Serialize(_handler(req));
                    }
                    catch (RpcBadRequestException ex)
                    {
                        status = 400;
                        payload = Serialize(IpcResponse.Fail(ex.Message));
                    }
                    catch (Exception ex)
                    {
                        status = 500;
                        payload = Serialize(IpcResponse.Fail($"internal error: {ex.Message}"));
                        AppLog.Error($"http rpc request failed: {ex.Message}");
                    }

                    await WriteJsonAsync(stream, status, payload, ct);
                    return;
                }

                await WriteJsonAsync(stream, 400,
                    Serialize(IpcResponse.Fail($"unsupported method/path: {method} {path} (use GET /health or POST /rpc)")), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止，静默退出
        }
        catch (Exception ex)
        {
            AppLog.Error($"http rpc client failed: {ex.Message}");
        }
    }

    private sealed class RpcBadRequestException : Exception
    {
        public RpcBadRequestException(string message) : base(message) { }
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json.IpcResponseOptions);

    private static string GetVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static int IndexOfHeadEnd(MemoryStream stream)
    {
        var bytes = stream.GetBuffer();
        var len = (int)stream.Length;
        for (var i = 0; i + 3 < len; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
            {
                return i; // 指向 \r\n\r\n 的起始 \r
            }
        }

        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, string json, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            500 => "Internal Server Error",
            _ => "OK",
        };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");
        await stream.WriteAsync(head, 0, head.Length, ct);
        await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
        await stream.FlushAsync(ct);
    }
}
