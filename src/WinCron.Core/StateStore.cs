using System.Text.Json;

namespace WinCron.Core;

/// <summary>
/// state.json 读写（{"globalEnabled": true}）；缺失或损坏时默认恢复调度。
/// </summary>
public sealed class StateStore
{
    public GlobalState Load()
    {
        try
        {
            var path = AppPaths.StateFile;
            if (!File.Exists(path))
            {
                return new GlobalState();
            }

            var state = JsonSerializer.Deserialize<GlobalState>(File.ReadAllText(path), Json.Default);
            return state ?? new GlobalState();
        }
        catch (Exception ex)
        {
            AppLog.Error($"state.json failed to load ({ex.Message}); defaulting to enabled");
            return new GlobalState();
        }
    }

    public void Save(GlobalState state)
    {
        var path = AppPaths.StateFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json.Default), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
