namespace WinCron.Core;

/// <summary>IPC 管道名（SPEC §6）。</summary>
public static class IpcProtocol
{
    public const string PipeName = "wincron_cs_ipc";
}

/// <summary>IPC 请求：一行 JSON。</summary>
public sealed class IpcRequest
{
    public string? Cmd { get; set; }
    public string? Target { get; set; }
    public string? Name { get; set; }
    public int N { get; set; }
}

/// <summary>IPC 响应：一行 JSON，{"ok":true,"data":...} 或 {"ok":false,"error":"..."}。</summary>
public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }

    public static IpcResponse Success(object? data) => new() { Ok = true, Data = data };

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
}
