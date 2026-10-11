# WinCron-CS 规格（C# 重写版 wincron）

目标：用 C#/.NET 8 重写 Go 版 wincron（`%USERPROFILE%\.wincron`），功能对齐、**数据格式完全兼容**（直接沿用旧数据文件，零迁移），并补上看门狗等稳定性机制。本文件是契约，实现不得偏离；有疑问按本文件为准。

## 1. 解决方案布局

```
D:\Code\wincron-cs\
  WinCron.sln
  SPEC.md
  README.md
  src\
    WinCron.Core\      net8.0 类库（无 UI 依赖）
    WinCron.App\       net8.0-windows WinForms（托盘+窗口），AssemblyName=wincron，OutputType=WinExe
    WinCron.Ctl\       net8.0 控制台，AssemblyName=wincronctl
```

NuGet 依赖（仅允许这三个）：
- `Cronos`（0.8.x 最新）— cron 解析
- `Microsoft.Data.Sqlite`（8.0.x，与 net8 对齐）— 日志库
- Core/Ctl 不引 WinForms 相关包；App 通过 `<UseWindowsForms>true</UseWindowsForms>`。

## 2. 数据目录（与旧版共用，必须兼容）

固定为 `%ProgramData%\wincron\`（`Environment.GetFolderPath(ProgramData)`）：

| 文件 | 说明 |
|---|---|
| `jobs.json` | 任务列表，camelCase，字段见 §3；读写必须与旧 Go 版互认 |
| `logs.sqlite` | 任务执行日志（SQLite），表结构见 §4；**必须沿用旧库与历史数据** |
| `state.json` | 新增：`{"globalEnabled": true}`，全局暂停/恢复状态（旧版不落盘，新版要落盘；旧版会忽略此文件） |
| `app.log` | 新增：应用自身运行日志（见 §7） |
| `crash-*.log` | 新增：致命异常现场（见 §7） |

## 3. Job 模型（jobs.json，camelCase，字段一个不多一个不少）

```csharp
class Job {
  string Id;          // GUID（小写带连字符）
  string Name;        // 必填
  string Folder;      // 可空字符串
  string Cron;        // 标准 5 段式或 "@reboot"
  string Command;     // 必填
  List<string> Args;  // 可空 → 写出为 [] 或省略均可
  string WorkDir;     // 空 = 数据目录
  string FlagProcessCreation; // "CREATE_NO_WINDOW" 或 ""
  int Timeout;        // 秒；0 = 无超时
  string ConcurrencyPolicy;   // "allow" | "skip" | "kill_old"（空 = "skip"）
  bool Enabled;
  bool RunAtStartup;
  int MaxConsecutiveFailures; // 0 = 不限
  int ConsecutiveFailures;
  long ExecutedCount;
  string LastExecutedAt;      // RFC3339 带本地偏移，如 2026-10-10T19:00:00+08:00；空串=未跑过
}
```

- 序列化用 System.Text.Json（camelCase）。`InheritEnv` 语义旧版由"字段缺省=继承"表达，现有 jobs.json 中无该字段且都是继承 → 新版同样**不新增此字段**，恒为继承（内部常量 true 即可，不留开关）。
- 读取时未知字段忽略；写回时按上面字段全集输出。写入必须原子：先写 `jobs.json.tmp` 再 `File.Move(tmp, jobs.json, overwrite:true)`。
- jobs.json 损坏/解析失败：把原文件改名 `jobs.json.corrupt-<yyyyMMdd-HHmmss>`，按空列表启动，app.log 记 error。**绝不能因配置损坏崩溃退出**。

## 4. 日志库（logs.sqlite，与旧版同结构）

连接后执行：`PRAGMA busy_timeout=3000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`

```sql
CREATE TABLE IF NOT EXISTS job_logs (
  id TEXT PRIMARY KEY,
  job_id TEXT NOT NULL,
  job_name TEXT NOT NULL,
  trigger_source TEXT NOT NULL DEFAULT '',
  command_line TEXT NOT NULL,
  started_at INTEGER NOT NULL,      -- unix 毫秒
  finished_at INTEGER NOT NULL,     -- unix 毫秒
  exit_code INTEGER NOT NULL,
  stdout TEXT NOT NULL,
  stderr TEXT NOT NULL,
  error TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_job_logs_job_id_started_at ON job_logs(job_id, started_at DESC);
```

- 只在任务结束时插入一条（运行中的条目仅在内存里提供给 UI/IPC）。
- `stdout`/`stderr` 各截断 16384 个字符后入库。
- 打开失败重试 3 次，仍失败则退化为 `Data Source=:memory:`（丢历史但不影响调度），app.log 记 error。
- 时间戳用 `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`。

## 5. 调度与执行语义（对齐旧版，逐条实现）

- cron 解析：Cronos `CronExpression.Parse(expr, CronFormat.StandardFlags)`；5 段标准式。`Trim()` 后等于 `@reboot` → 视为开机任务（RunAtStartup=true，不参与 cron 调度）。解析失败的任务不调度、app.log 记 error，不影响其他任务。
- 调度器：每任务一个 `System.Threading.Timer`，dueTime = `CronExpression.GetNextOccurrence(DateTime.Now, TimeZoneInfo.Local) - now`；触发后立即重算下次。到点后 `Task.Run` 执行（不占定时器线程）。全局/任务禁用时销毁定时器，启用时重建。
- 睡眠唤醒：定时器到点已过期会立即触发（.NET 默认），即补跑一次唤醒后错过的点，不做补扫队列——与旧版一致。
- 执行（对齐 Go 版 `execute()`）：
  - `ProcessStartInfo`：`UseShellExecute=false`；`FileName=Command`；`ArgumentList.AddRange(Args)`；`WorkingDirectory=WorkDir 或数据目录`；环境变量默认继承（`EnvironmentVariables` 不动）；`CreateNoWindow = (FlagProcessCreation=="CREATE_NO_WINDOW")`。
  - stdout/stderr 重定向捕获，UTF-8。
  - `Timeout>0`：到点 `Process.Kill()`（只杀进程本身，与旧版一致），`ExitCode=-1`，`Error="timeout after <N>s"`。
  - 正常等待结束：退出码取 `Process.ExitCode`；若 `Win32Exception`/启动失败 → `ExitCode=-1`，`Error=异常消息`。
  - 结束后入库一条日志、更新计数：`ExecutedCount++`、`LastExecutedAt=FinishedAt`；成功 → `ConsecutiveFailures=0`；失败 → `++`；若 `Enabled && MaxConsecutiveFailures>0 && ConsecutiveFailures>=Max` → 自动 `Enabled=false`（app.log 记 warning）。随后持久化 jobs.json。
- 并发策略（对齐 Go）：
  - `allow`：可并行。
  - `skip`：已在跑 → cron 触发静默跳过；UI/IPC 触发报错 "job is already running"。
  - `kill_old`：先 `Kill()` 所有该任务的运行实例再启动。
- 触发来源常量（写入 `trigger_source`）：`"cron"`、`"ui"`、`"ipc"`、`"preview"`、`"hotkey"`（hotkey 本版不实现，仅保留常量）；开机任务用 `"cron"`。
- 启动时：加载 jobs.json → 对全部启用且 `RunAtStartup` 的任务异步执行一轮（trigger=cron）→ 为其余启用任务建定时器。
- 全局启停：`state.json` 持久化；禁用时清掉所有定时器，启用时按当前 jobs 重建。

## 6. IPC 与 CLI（wincronctl）

- 管道：`NamedPipeServerStream`，名 `wincron_cs_ipc`（`PipeOptions.Asynchronous`，本用户会话用，无需 ACL）。请求一行 JSON、回一行 JSON、断开。
- 请求：`{"cmd":"...","target":"...","name":"...","n":50}`；响应：`{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`。
- 服务端命令：
  - `status` → `{"globalEnabled":bool,"jobs":[{name,folder,enabled,consecutiveFailures,executedCount,lastExecutedAt,nextRunAt}]}`
  - `list` → 任务全量 JSON
  - `enable`/`disable`（target 可空）→ 空=全局开关（写 state.json 并重建/清空定时器）；target 先按任务名精确匹配（忽略大小写），无命中再按 folder 匹配（对该文件夹全部任务生效）；返回受影响清单
  - `run`（name）→ 按名或 id 找任务立即执行（trigger=ipc）；已在跑且策略 skip → 报错
  - `logs`（name 可空、n 默认 50）→ tail 条目（含运行中内存条目，新的在前）
  - `open` → 显示主窗口
  - `quit` → 优雅退出（保存状态、释放图标、`Application.Exit`）
- `wincronctl` 子命令（无参数时打印用法，退出码：0 成功 / 1 出错 / 2 应用未在运行）：
  - `status`：连管道；连不上打印 `wincron is not running` 并退出码 2；成功打印全局状态 + 每任务一行摘要（名称、启停、连续失败、下次运行）。
  - `list` / `enable [target]` / `disable [target]` / `run <name>` / `logs [name] [-n N]` / `open` / `quit`：直连转发。
  - `ensure`：先试连管道，通 → 打印 `wincron is running`；不通 → 以 `Process.Start` 隐藏启动**与自己同目录的** `wincron.exe`，最长等 15 秒管道就绪，成功打印 `wincron started`，超时退出码 1。**给计划任务看门狗用，必须幂等、无窗口。**

## 7. 稳定性要求（本项目的核心动机，逐条落实）

1. `AppLog`：追加写 `app.log`，行格式 `2026-10-11T09:30:00+08:00 [INFO] ...`（本地时间 RFC3339）；超过 5MB 轮转为 `app.log.1`（直接覆盖旧轮转）。记录：启动/退出、任务开始/结束（含退出码与耗时）、自动禁用、状态切换、异常。所有写日志异常自行吞掉。
2. 全局异常处理：`Application.ThreadException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException` → 写 `crash-<时间戳>.log`（异常类型、消息、完整堆栈）+ app.log `[FATAL]/[ERROR]` 行。UI 线程异常：记日志并尽量继续运行；`AppDomain.UnhandledException`（致命）：记日志后 `Environment.Exit(1)` —— 依靠看门狗 ≤5 分钟拉起。
3. 启动健壮：上述 §3/§4 的损坏退化路径；数据目录不可建（权限）→ 弹托盘气球提示并仍保持进程存活（调度禁用）。
4. 单实例：Mutex `Local\wincron_cs_single_instance`。二次启动：尝试 IPC `open` 后退出（把窗口带到前台）。
5. 主窗口关闭 = 隐藏到托盘（不退出）；退出仅经托盘菜单或 `wincronctl quit`。
6. `EnsureHandle` 类细节：NotifyIcon 图标用 `SystemIcons.Application`；退出时必须 `Dispose` 托盘图标，避免僵尸图标。

## 8. UI（WinForms，朴素但完整，全部手写布局不用设计器）

- 主窗口（约 800x520，`FormBorderStyle=Sizable`，最小尺寸限制）：顶部状态条显示"调度：运行中/已暂停"；两个 Tab：
  - **任务**：ListView(Details, FullRowSelect, GridLines) 列：名称/文件夹/Cron/下次运行/上次运行/状态(启用|禁用|连续失败N)/已执行；底部按钮：新建、编辑、删除（确认框）、立即运行、启用/禁用（随所选切换文案）、刷新；双击行=编辑。刷新时全量重查。
  - **日志**：任务下拉（含"全部"）+ 条数输入（默认 100）+ 查询按钮；条目 ListView(Details)：开始时间(yyyy-MM-dd HH:mm:ss)/触发/退出码/错误摘要；下方只读多行 TextBox（Consolas）显示所选条目的 stdout/stderr/error 全文；按钮：清空该任务日志、清空全部日志（均需确认）。运行中的条目也显示（退出码列显示"运行中"）。
- JobEditorForm（对话框，OK/取消）：名称、文件夹、Cron（旁有"预览下次运行"按钮，解析失败红字提示）、命令、参数（多行 TextBox，**每行一个参数**）、工作目录、超时秒（NumericUpDown 0~86400）、并发策略（ComboBox：allow/skip/kill_old）、最大连续失败（NumericUpDown 0=不限）、CREATE_NO_WINDOW 复选框、启用复选框。校验：名称/命令必填、Cron 可解析（@reboot 放行）。
- 托盘：菜单 = 打开主窗口 /（分隔）/ 每个启用任务一个"立即运行 <name>" /（分隔）/ 暂停调度（或"恢复调度"，随状态切换）/ 退出。双击图标=打开窗口。任务失败（ExitCode!=0）时气球提示一次（节流：同任务 10 分钟内不重复）。
- UI 与 Core 通过 `CronService` 门面交互（事件：JobStarted/JobFinished/JobsChanged，UI 线程 Marshal 后刷新）。

## 9. 明确不做（与旧版差异）

- 不做全局热键、不做导入/导出 YAML、不做日志分页（只 tail N 条）、不做开机自启的 UI 开关（由外部计划任务负责，见 README）。
- 不迁移任何数据：直接读写现有 `%ProgramData%\wincron` 下的文件。

## 11. 应用图标（assets/icon.svg → assets/app.ico）

- `assets/icon.svg` 为唯一设计源（已提供，不得改设计）。
- 生成 `assets/app.ico`：多尺寸 PNG 条目（256/64/48/32/16），**透明背景**。工具约束：本机无 magick/inkscape/resvg；用 Playwright（npm 包装时必须先设 `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`，运行时 `channel: 'msedge'`，**禁止** `playwright install`）无头截图（`omitBackground: true`）得到各尺寸透明 PNG，再用 bun 脚本手写 ICO 容器打包（ICONDIR + ICPENTRY + PNG 裸块，width 字节 256 记 0）。转换脚本放 `tools/`，生成物 `assets/app.ico` 留在项目里。
- App 工程：csproj `<ApplicationIcon>` 指向该 ico（exe 文件图标）；同一 ico 以 `EmbeddedResource` 内嵌（LogicalName `WinCron.app.ico`），NotifyIcon 用 `new Icon(assembly.GetManifestResourceStream("WinCron.app.ico"))` 加载（保留多尺寸，托盘 16x16 清晰），替换 `SystemIcons.Application`；退出 Dispose 逻辑不变。
- 生成后程序化自检（不依赖人工看图）：条目数=5、各条目尺寸正确、PNG 未全透明（存在非零 alpha 且非单色）。

## 12. HTTP RPC（agent 调试通道）

- 目的：给自动化 agent 提供可 `curl` 的调试/控制通道。**命令集与 IPC 完全一致，共用同一分发器**，不新造语义。
- 传输：`TcpListener` 实现最小 HTTP/1.1（**不用** HttpListener/http.sys，避免 URLACL 权限问题），只绑定 `127.0.0.1`。端口默认 `17877`；`state.json` 可加 `"rpcPort": <int>` 覆盖（`0` = 关闭 RPC；字段缺省 = 17877）。启动时读取一次，改端口需重启。
- 端点：
  - `GET /health` → `{"ok":true,"data":{"pid":<int>,"version":"1.1.0"}}`（直接返回，不过命令分发器）
  - `POST /rpc` → body 为与管道一致的请求 JSON（`{"cmd","target","name","n"}`），响应与管道一致的单行 JSON 信封；一个连接一个请求，响应带 `Connection: close`。
- 行为约束：方法/路径不匹配 → HTTP 400 + JSON 错误信封；body 按 Content-Length 读取（上限 64KB，超限 400）；响应头 `Content-Type: application/json; charset=utf-8`；每请求 try/catch，异常 → HTTP 500 + `{"ok":false,"error":...}`，服务绝不因单请求崩溃；启动成功 app.log 记一条 `[INFO] http rpc listening on 127.0.0.1:<port>`，单个请求不记日志（防刷屏），仅处理异常记 ERROR。
- 版本号：App csproj 设 `<Version>1.1.0</Version>`，/health 从程序集读。
- 安全边界（写进 README）：仅回环绑定；本机任何进程都可调用（含 quit/run），与现有命名管道同等暴露面，个人机器可接受。

## 10. 验收标准（子 agent 自检）

1. `dotnet build D:\Code\wincron-cs\WinCron.sln -c Release` 0 error（warning 允许）。
2. Core 的 cron/并发/超时/连续失败逻辑与 §5 一一对应（代码评审可见，无需单测框架；可在 `tests/` 下放一个纯控制台 scratch 工程自测 Cronos 行为后删除，或直接保留一个 `WinCron.Core.Tests` 亦可，但不强制）。
3. 未运行任何已构建的 exe、未触碰 `C:\ProgramData\wincron`、未执行任何 git 命令、未改动 `D:\Code\wincron-cs` 之外的任何文件。
