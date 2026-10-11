using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinCron.Core;

/// <summary>
/// NamedPipe IPC 服务端（SPEC §6）：一行 JSON 请求、一行 JSON 响应、断开。
/// 所有异常自吞（记 app.log），绝不让 IPC 问题拖垮调度。
/// </summary>
public sealed class IpcServer
{
    private readonly Func<IpcRequest, IpcResponse> _handler;
    private readonly Action _onQuit;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public IpcServer(Func<IpcRequest, IpcResponse> handler, Action onQuit)
    {
        _handler = handler;
        _onQuit = onQuit;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(connected));
            }
            catch (OperationCanceledException)
            {
                try { pipe?.Dispose(); } catch { }
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error($"ipc server loop error: {ex.Message}");
                try { pipe?.Dispose(); } catch { }
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        bool quit = false;

        // 读请求阶段：客户端连上后不发请求直接断开（连接探测、网络抖动）属正常现象，
        // 降级为 [INFO]，不刷 ERROR；真正的处理/响应阶段异常才记 ERROR。
        string? line;
        try
        {
            using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                line = await reader.ReadLineAsync();
            }

            if (line == null)
            {
                // EOF：未发请求即断开
                AppLog.Info("ipc client disconnected before request");
                return; // finally 负责清理
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"ipc client disconnected before request: {ex.Message}");
            return; // finally 负责清理
        }

        try
        {
            IpcResponse resp;
            if (string.IsNullOrWhiteSpace(line))
            {
                resp = IpcResponse.Fail("empty request");
            }
            else
            {
                IpcRequest? req = null;
                try
                {
                    req = JsonSerializer.Deserialize<IpcRequest>(line, Json.Default) ?? new IpcRequest();
                    resp = _handler(req);
                }
                catch (Exception ex)
                {
                    resp = IpcResponse.Fail(ex.Message);
                }

                // quit：先回响应、断开管道，再触发退出
                quit = resp.Ok && string.Equals(req?.Cmd, "quit", StringComparison.OrdinalIgnoreCase);
            }

            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true })
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, Json.IpcResponseOptions));
                pipe.WaitForPipeDrain();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"ipc client handling failed: {ex.Message}");
        }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
            try { pipe.Dispose(); } catch { }
        }

        if (quit)
        {
            try { await Task.Delay(150); } catch { }
            try { _onQuit(); }
            catch (Exception ex)
            {
                AppLog.Error($"quit handling failed: {ex.Message}");
            }
        }
    }
}
