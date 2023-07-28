using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace DiegoG.AutoUpdater;

public interface IUpdateSource
{
    public void UseOptions(JsonDocument options);
    public Task<bool> CheckUpdate(VersionHash hash);
    public Task<VersionHash?> Update(ILogger log, DirectoryInfo target);
    public virtual static object? ExampleOptions { get; }
    public virtual static string? Description { get; }
}
