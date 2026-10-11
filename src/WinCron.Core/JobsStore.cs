using System.Text.Json;

namespace WinCron.Core;

/// <summary>
/// jobs.json 读写：原子写（tmp + File.Move overwrite），损坏时改名退化并按空列表启动（SPEC §3）。
/// </summary>
public sealed class JobsStore
{
    public List<Job> Load()
    {
        var path = AppPaths.JobsFile;
        try
        {
            if (!File.Exists(path))
            {
                return new List<Job>();
            }

            var json = File.ReadAllText(path);
            var jobs = JsonSerializer.Deserialize<List<Job>>(json, Json.Default) ?? new List<Job>();
            foreach (var job in jobs)
            {
                job.Normalize();
            }

            return jobs;
        }
        catch (Exception ex)
        {
            // 损坏/解析失败：改名保存现场，按空列表启动，绝不崩溃退出
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{path}.corrupt-{stamp}";
            try
            {
                File.Move(path, backup);
            }
            catch
            {
                backup = "(rename failed)";
            }

            AppLog.Error($"jobs.json failed to load ({ex.Message}); renamed to {backup}, starting with an empty job list");
            return new List<Job>();
        }
    }

    public void Save(List<Job> jobs)
    {
        var path = AppPaths.JobsFile;
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(jobs, Json.Indented);
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
