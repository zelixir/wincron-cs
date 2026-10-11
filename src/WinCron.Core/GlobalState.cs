namespace WinCron.Core;

/// <summary>
/// state.json 模型。旧版只认识 globalEnabled（新版落盘的 rpcPort 会被旧版忽略）。
/// </summary>
public sealed class GlobalState
{
    public bool GlobalEnabled { get; set; } = true;

    /// <summary>
    /// HTTP RPC 监听端口（SPEC §12）：仅绑定 127.0.0.1；
    /// 0 = 关闭 RPC；字段缺省 = 默认 17877。启动时读取一次，改端口需重启。
    /// </summary>
    public int RpcPort { get; set; } = 17877;
}
