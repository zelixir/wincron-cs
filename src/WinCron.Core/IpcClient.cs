using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinCron.Core;

/// <summary>IPC 客户端（App 二次实例用：向已运行实例发 open）。</summary>
public static class IpcClient
{
    public static bool TrySend(IpcRequest request, int timeoutMs, out IpcResponse? response)
    {
        response = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json.Default) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = reader.ReadLine();
            if (line == null)
            {
                return false;
            }

            response = JsonSerializer.Deserialize<IpcResponse>(line, Json.Default);
            return response?.Ok == true;
        }
        catch
        {
            return false;
        }
    }
}
