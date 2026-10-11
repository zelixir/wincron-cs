# wincron-cs

Windows 托盘定时任务工具 wincron 的 C#/.NET 8 重写版。规格契约见 [SPEC.md](SPEC.md)，实现以 SPEC 为准。

与旧 Go 版（`%USERPROFILE%\.wincron`）**数据格式完全兼容**：直接共用 `%ProgramData%\wincron\` 下的
`jobs.json` / `logs.sqlite`，零迁移。`state.json`、`app.log`、`crash-*.log` 为新版新增，旧版会忽略。

> 注意：新版与旧版**不建议同时运行**（两者都会读写 jobs.json 与 logs.sqlite）。切换前先退出旧版。

## 组成

| 工程 | 目标框架 | 产物 | 说明 |
|---|---|---|---|
| `src/WinCron.Core` | net8.0 | WinCron.Core.dll | 无 UI 类库：Job 模型、jobs.json/state.json 存取、SQLite 日志库、Cronos 调度器、进程执行器、IPC 服务端、HTTP RPC |
| `src/WinCron.App` | net8.0-windows | `wincron.exe` | WinForms 托盘 + 主窗口（WinExe） |
| `src/WinCron.Ctl` | net8.0 | `wincronctl.exe` | 控制台 CLI（status/list/enable/disable/run/logs/open/quit/ensure） |

NuGet 依赖仅两个：`Cronos 0.8.4`（cron 解析）、`Microsoft.Data.Sqlite 8.0.11`（日志库）。

## 构建

```bat
dotnet build D:\Code\wincron-cs\WinCron.sln -c Release
```

要求 .NET SDK 8.0.x（仓库已用 `global.json` 固定到 8.0.4xx）。

## 发布

框架依赖方式（需要目标机装有 .NET 8 桌面运行时；两个 exe 必须发布到**同一目录**，
`wincronctl ensure` 依赖同目录的 `wincron.exe`）：

```bat
dotnet publish src\WinCron.App -c Release -o publish
dotnet publish src\WinCron.Ctl -c Release -o publish
```

得到 `publish\wincron.exe` 与 `publish\wincronctl.exe`。自包含方式加
`-r win-x64 --self-contained true`（体积更大但无需装运行时）。

## 计划任务看门狗（建议配置）

新版依赖外部计划任务做看门狗与开机自启（程序内不再提供开机自启开关）。
核心是 `wincronctl ensure`：应用已在跑则直接返回（幂等）；没在跑则以无窗口方式拉起同目录
`wincron.exe`，最长等 15 秒管道就绪。

用图形界面"任务计划程序"或命令行创建（**以当前用户运行、且勾选"只在用户登录时运行"**，
这样托盘图标会出现在用户会话里；用 SYSTEM 运行则托盘不可见）：

```bat
schtasks /Create /F ^
  /TN "WinCron Watchdog" ^
  /SC MINUTE /MO 5 ^
  /TR "\"C:\deploy\wincron\wincronctl.exe\" ensure" ^
  /RU "%USERNAME%"
```

- 每 5 分钟跑一次 `ensure`：正常时无任何副作用；进程崩溃/被杀后 ≤5 分钟自动拉起（`--hidden` 启动，只留托盘不弹窗）。
- 开机自启：再建一条"登录时"触发的同款任务，或直接依赖看门狗在登录后首个周期拉起。

## wincronctl 用法

```text
wincronctl status                 全局状态 + 每任务摘要
wincronctl list                   全部任务 JSON
wincronctl enable [target]        启用（空=全局；否则按任务名或文件夹）
wincronctl disable [target]       禁用（同上）
wincronctl run <name>             立即运行（按名称或 id）
wincronctl logs [name] [-n N]     最近 N 条日志（默认 50）
wincronctl open                   显示主窗口
wincronctl quit                   优雅退出
wincronctl ensure                 看门狗幂等拉起（见上）
```

退出码：`0` 成功 / `1` 出错 / `2` wincron 未在运行（打印 `wincron is not running`）。

## 数据目录（`%ProgramData%\wincron\`）

| 文件 | 说明 |
|---|---|
| `jobs.json` | 任务列表（camelCase，与旧版互认；损坏自动改名 `jobs.json.corrupt-<时间戳>` 并按空列表启动） |
| `logs.sqlite` | 执行日志（WAL；打不开自动退化内存库） |
| `state.json` | 全局暂停/恢复状态 |
| `app.log` / `app.log.1` | 应用日志（>5MB 轮转） |
| `crash-<时间戳>.log` | 致命异常现场 |

`wincron.exe` 支持参数 `--hidden`：启动即只留托盘、不弹主窗口（`ensure` 内部使用）。

## HTTP RPC（agent 调试通道，SPEC §12）

除命名管道外，wincron 另开一个 HTTP 调试通道：`TcpListener` 实现的最小 HTTP/1.1，
**只绑定 `127.0.0.1`**，默认端口 `17877`。端口可在 `%ProgramData%\wincron\state.json` 加
`"rpcPort": <int>` 覆盖（`0` = 关闭 RPC；字段缺省 = 17877），启动时读取一次，改端口需重启。

命令集与 `wincronctl`/命名管道**完全一致**（`POST /rpc` 即管道请求 JSON）：

```bash
# 健康检查（进程号 + 版本）
curl http://127.0.0.1:17877/health

# 查看全局状态与任务摘要
curl -X POST http://127.0.0.1:17877/rpc -d '{"cmd":"status"}'

# 立即运行某任务
curl -X POST http://127.0.0.1:17877/rpc -d '{"cmd":"run","name":"rss-filter"}'

# 最近 20 条日志
curl -X POST http://127.0.0.1:17877/rpc -d '{"cmd":"logs","n":20}'
```

（Windows cmd 下 JSON 内的双引号需转义：`-d "{\"cmd\":\"status\"}"`。）

响应为与管道一致的单行 JSON 信封：`{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`；
方法/路径不匹配、body 超 64KB 返回 HTTP 400，单请求内部异常返回 HTTP 500（均带 JSON 信封，
不影响调度主流程）。单个请求不写 app.log（防刷屏），仅异常记 ERROR。

**安全边界**：仅回环绑定（127.0.0.1），本机任何进程都可以调用——包括 `run`、`enable/disable`
乃至 `quit`，与现有命名管道是同等暴露面；不提供鉴权，个人机器上可接受。**不要**通过端口
转发/反向代理把它暴露到本机之外。

## 应用图标（SPEC §11）

设计源为 `assets/icon.svg`（唯一来源，勿手改）。`assets/app.ico` 由它再生成：
`node tools/render-icon.mjs`（Playwright + 系统 Edge 无头渲染 256/64/48/32/16 五个透明 PNG，
需先 `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm i playwright`，**禁止** `playwright install`）
→ `bun tools/build-ico.ts`（手写 ICO 容器打包并自检）。exe 图标与托盘图标（内嵌资源
`WinCron.app.ico`）均来自该文件。
