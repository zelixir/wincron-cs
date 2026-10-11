using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCron.Core;

/// <summary>
/// 统一的 JSON 序列化配置：camelCase（与旧 Go 版 jobs.json 互认）、读取时忽略大小写与未知字段。
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>jobs.json 落盘用（带缩进，便于人工查看）。</summary>
    public static readonly JsonSerializerOptions Indented = new(Json.Default)
    {
        WriteIndented = true,
    };

    /// <summary>IPC 响应用：null 字段不输出（ok/error/data 三选二）。</summary>
    public static readonly JsonSerializerOptions IpcResponseOptions = new(Json.Default)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
