﻿using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using DiegoG.AutoUpdater.UpdateSources;
using Serilog;

namespace DiegoG.AutoUpdater;

#warning Symbolic links are not taken into account when cleaning up the cleanup list. Since this is a root tool, isn't this something that should be fixed?

public static class Program
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true
    };

    public static string LogDir { get; }
    public static ImmutableArray<UpdatingOptions> Options { get; }
    public static Dictionary<string, Func<IUpdateSource>> UpdateSources { get; }

    [ThreadStatic]
    private static StringBuilder? sharedSb;

    public static StringBuilder GetSharedStringBuilder()
        => sharedSb ??= new();

    static Program()
    {
        LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiegoG.AutoUpdater", "logs");
        Directory.CreateDirectory(LogDir);

        var logfile = Path.Combine(LogDir, ".log");

        const string format = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}]{NewLine} > {Message:lj}{NewLine}{Exception}";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
#if !DEBUG
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information, outputTemplate: format)
#else
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Verbose, outputTemplate: format)
#endif
            .WriteTo.File(logfile, rollingInterval: RollingInterval.Day, outputTemplate: format)
            .WriteTo.LocalSyslog("DiegoG.AutoUpdater", outputTemplate: format)
            .CreateLogger();

        Log.Information("Log file: {file}", logfile);

        try
        {
            var optionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiegoG.AutoUpdater");
            Directory.CreateDirectory(optionsDir);

            Log.Information("Looking for configuration file 'options.json' in {dir}", optionsDir);
            var optionsFile = Path.Combine(optionsDir, "options.json");

            if (File.Exists(optionsFile) is false)
            {
                Log.Warning("Options file does not exist, creating a new one");
                using (var strm = File.Create(optionsFile))
                    JsonSerializer.Serialize(strm, Array.Empty<UpdatingOptions>(), options: JsonOptions);
            }

            var exampleFileTxt = Path.Combine(optionsDir, "README.txt");
            var exampleFileJson = Path.Combine(optionsDir, "options.example.json");
            if (File.Exists(exampleFileTxt) is false)
                File.WriteAllText(exampleFileTxt, "Thanks for using my tool! A quick explanation:\n - options.json is created automatically on first run\n - It's an /array/ of JSON objects, not a single object\n - options.example.json contains an example for a SINGLE options object, and when thrown into options.json, should be put inside an array.\n - An array in JSON is defined by an opening bracket '[' and a closing bracket ']'. Inside of it, any number of JSON objects can exist, each separated by a comma. Don't put a comma for the last one.");

            if (File.Exists(exampleFileJson) is false)
                using (var examplestream = File.Create(exampleFileJson))
                    JsonSerializer.Serialize(examplestream, new UpdatingOptions()
                    {
                        CleanupAll = true,
                        CleanUpExceptions = new HashSet<string>()
                        {
                            "Example.file",
                            "Somefile.txt",
                            "PleasePreserveMe.help"
                        },
                        ProcessName = "MyProcess",
                        CleanUpList = new HashSet<string>()
                        {
                            "Deletable.file",
                            "Sometransientfile.txt",
                            "PleaseKillMe.help"
                        },
                        ExecuteCommand = "Command --to-execute --at the-end",
                        MessagePipeName = "Unused.",
                        PermitKillProcess = true,
                        SourceName = "github-release or something else idk",
                        SourceOptions = JsonDocument.Parse("{ \"IsThisAJsonObject\": true, \"CanIPutWhateverHere\": true }"),
                        TargetDirectory = "C:\\TheDirectory\\IWantTo\\Update"
                    }, options: JsonOptions);

            try
            {
                using (var optionsFileStream = File.OpenRead(optionsFile))
                    Options = ImmutableArray.Create(JsonSerializer.Deserialize<UpdatingOptions[]>(optionsFileStream, options: JsonOptions));
            }
            catch(JsonException e)
            {
                Log.Fatal(e, "The Options file does not contain valid JSON data and cannot be read. Repair the file and try again.");
                Environment.Exit(-1);
            }

            UpdateSources = new();
            foreach (var (type, attr) in AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(x => x.GetTypes())
                .Select(x => (Type: x, Attr: x.GetCustomAttribute<UpdateSourceAttribute>()!))
                .Where(x => x.Attr is not null))
            {
                if (type is not { IsAbstract: false, IsClass: true, IsPublic: true, ContainsGenericParameters: false } ||
                    type.IsAssignableTo(typeof(IUpdateSource)) is false)
                    throw new InvalidDataException("A type decorated with the 'UpdateSourceAttribute' MUST be a non abstract, non generic class, and MUST be public; it also MUST implement the interface 'IUpdateSource'");

                UpdateSources.Add(attr.SourceName, () => (IUpdateSource)(Activator.CreateInstance(type)
                        ?? throw new TypeLoadException($"Could not load a new instance of type {type.FullName}")));
            }
        }
        catch(Exception e)
        {
            Log.Fatal(e, "An unexpected error ocurred during initialization");
            Log.CloseAndFlush();
            Environment.Exit(-1);
        }
    }

    private static ILogger CreateLogger(string sourceName, string processName)
    {
        var logfile = Path.Combine(LogDir, sourceName, ".log");
        const string format = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Source: {Source}, Process: {Process}]{NewLine} > {Message:lj}{NewLine}{Exception}";
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
#if !DEBUG
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information, outputTemplate: format)
#else
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Verbose, outputTemplate: format)
#endif
            .WriteTo.File(logfile, rollingInterval: RollingInterval.Day, outputTemplate: format)
            .WriteTo.LocalSyslog("DiegoG.AutoUpdater", outputTemplate: format)
            .Enrich.WithProperty("Source", sourceName)
            .Enrich.WithProperty("Process", processName)
            .CreateLogger();

        logger.Information("Log file: {file}", logfile);
        return logger;
    }

    private static void CleanupDirectory(string dir, string basedir, HashSet<string> checks, HashSet<string>? exceptions)
    {
        dir = Path.GetFullPath(dir);
        basedir = Path.GetFullPath(basedir);

        if (dir.Contains(basedir) is false || checks.Add(dir) is false)
            return;

        if (exceptions is not null)
        {
            foreach (var d in Directory.GetDirectories(dir).Where(x => exceptions.Contains(Path.GetDirectoryName(x)!) is false && exceptions.Contains(x) is false))
                CleanupDirectory(d, basedir, checks, exceptions);

            foreach (var f in Directory.GetFiles(dir).Where(x => string.Equals(x, "versionhash", StringComparison.OrdinalIgnoreCase) is false && exceptions.Contains(Path.GetFileName(x)) is false && exceptions.Contains(x) is false)) 
                if (File.Exists(f))
                    File.Delete(f);
        }
        else
        {
            foreach (var d in Directory.GetDirectories(dir))
                CleanupDirectory(d, basedir, checks, exceptions);

            foreach (var f in Directory.GetFiles(dir).Where(x => string.Equals(x, "versionhash", StringComparison.OrdinalIgnoreCase) is false))
                if (File.Exists(f))
                    File.Delete(f);
        }
    }

    private static async Task Main(string[] args)
    {
        try
        {
            Log.Information("Starting DiegoG.AutoUpdater");
            Log.Debug("Iterating through options");

            if (Options.Length is <= 0)
            {
                Log.Information("There are no options available and nothing to update, finished DiegoG.AutoUpdater");
                return;
            }

            int errors = 0;
            int successes = 0;
            int skips = 0;
            foreach (var options in Options)
            {
                try
                {
                    Log.Information("Attempting to update {process} via {source}", options.ProcessName, options.SourceName);

                    if (UpdateSources.TryGetValue(options.SourceName, out var source) is false)
                    {
                        Log.Error("Could not find a source under the name of {source} for {process}", options.SourceName, options.ProcessName);
                        errors++;
                        continue;
                    }

                    Log.Verbose("Instantiating source");
                    var src = source.Invoke();

                    Log.Debug("Checking if an update is available");
                    Log.Verbose("Looking for a versionhash file");

                    VersionHash hash;

                    var dir = new DirectoryInfo(options.TargetDirectory);
                    dir.Create();

                    var hashfile = Directory.EnumerateFiles(options.TargetDirectory, "versionhash").FirstOrDefault();
                    if (hashfile is null || File.Exists(hashfile) is false)
                    {
                        Log.Information("Could not find a versionhash file for the target, assuming null");
                        hash = default;
                    }
                    else
                        using (var hashfilestream = File.OpenRead(hashfile))
                            hash = VersionHash.LoadFrom(hashfilestream);

                    Log.Information("Preparing to update {process} via {source}, with a target directory of {target}", options.ProcessName, options.SourceName, options.TargetDirectory);

                    Log.Debug("Configuring {source} for {process}", options.SourceName, options.ProcessName);
                    src.UseOptions(options.SourceOptions);

                    Log.Debug("Verifying if an update is necessary");
                    if (await src.CheckUpdate(hash) is false)
                    {
                        Log.Information("Local version is up-to-date. Continuing with next item");
                        skips++;
                        continue;
                    }

                    Log.Debug("Finding processes under the name of {name}", options.ProcessName);
                    var proc = Process.GetProcessesByName(options.ProcessName);

                    Log.Debug("Found {count} active processes", proc.Length);

                    if (proc.Length > 0)
                    {
                        if (options.PermitKillProcess)
                        {
                            Log.Debug("Killing the process is allowed, terminating...");
                            foreach (var p in proc)
                            {
                                if (p.CloseMainWindow() is false)
                                    p.Kill();
                            }
                        }
                        else
                        {
                            Log.Warning("Killing the processes is disallowed, the update cannot continue. Continuing with next item.");
                            skips++;
                            continue;
                        }
                    }

                    Log.Debug("Commencing pre-update cleanup");
                    if (options.CleanupAll)
                    {
                        Log.Debug("Cleaning up...");
                        CleanupDirectory(options.TargetDirectory, options.TargetDirectory, new HashSet<string>(), options.CleanUpExceptions);
                    }

                    if (options.CleanUpList is not null)
                    {
                        Log.Debug("Cleaning up entries from cleanup list");
                        foreach (var fse in options.CleanUpList)
                            if (string.Equals(fse, "versionhash", StringComparison.OrdinalIgnoreCase))
                                continue;
                            else if (Directory.Exists(fse))
                                Directory.Delete(fse, true);
                            else if (File.Exists(fse))
                                File.Delete(fse);
                    }

                    Log.Information("Succesfully cleaned up target directory: {target}", options.TargetDirectory);

                    Log.Debug("Creating a new logger for {source} for {process}", options.SourceName, options.ProcessName);
                    var logger = CreateLogger(options.SourceName, options.ProcessName);

                    Log.Information("Updating {process} via {source}", options.ProcessName, options.SourceName);
                    var versionHashResult = await src.Update(logger, dir);

                    if (versionHashResult is VersionHash newversionhash)
                    {
                        Log.Debug("Creating new versionhash file");
                        using (var nvhStream = File.Open(Path.Combine(dir.FullName, "versionhash"), FileMode.Create))
                            newversionhash.CopyTo(nvhStream);
                        Log.Debug("Succesfully created new versionhash file");
                    }
                    else
                    {
                        Log.Warning("The update operation failed, no new versionhash created");
                        errors++;
                        continue;
                    }

                    if (options.ExecuteCommand is not null)
                        Process.Start(new ProcessStartInfo(options.ExecuteCommand)
                        {
                            UseShellExecute = options.UseShellExecute
                        });

                    Log.Information("Succesfully updated {process} via {source}", options.ProcessName, options.SourceName);
                    successes++;
                }
                catch(Exception e)
                {
                    Log.Error(e, "An error ocurred while trying to update {process} via {source}: {msg} --- {type}", options.ProcessName, options.SourceName, e.Message, e.GetType().Name);
                    errors++;
                    continue;
                }
            }

            if (errors > 0)
                Log.Warning("Finished DiegoG.AutoUpdater with {errors} errors, {skips} skips and {successes} successes, please review log file for details", errors, skips, successes);
            else
                Log.Information("Finished DiegoG.AutoUpdater with {successes} updates and {skips} skips", successes, skips);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}