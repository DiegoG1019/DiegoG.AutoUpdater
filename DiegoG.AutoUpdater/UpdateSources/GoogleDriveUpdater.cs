using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Permissions;
using Microsoft.IO;
using Octokit;

namespace DiegoG.AutoUpdater.UpdateSources;

public sealed class GoogleDriveUpdater : IUpdateSource
{
    public sealed record class GoogleDriveUpdaterOptions(string ApiKey, string Folder);

    public void UseOptions(JsonDocument options)
    {
        var op = JsonSerializer.Deserialize<GoogleDriveUpdaterOptions>(options);

        if (op.ApiKey is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(op.ApiKey)) throw new ArgumentException("ApiKey cannot be empty or just be whitespace", nameof(options));
        Drive = new(new Google.Apis.Services.BaseClientService.Initializer()
        {
            ApplicationName = "WardianDesktop.SynchronizationService",
            ApiKey = apiKey
        });
    }

    public Task<bool> CheckUpdate(VersionHash hash)
    {
        throw new NotImplementedException();
    }

    public Task<VersionHash?> Update(ILogger log, DirectoryInfo target)
    {
        throw new NotImplementedException();
    }

    public static object ExampleOptions { get; } = new GitHubReleaseUpdaterOptions(
        "Ryan",
        "Reynolds.API",
        0,
        "MyToken",
        "TheFileYouActuallyWant.zip",
        false,
        false
    );

    public static string Description { get; } = """
        This Updater goes into the target repository's releases and creates a hash based on the tag of the latest release by date.
        Options:
            - RepositoryOwner: Do not set if RepositoryId is set. This and RepositoryOwner must both be set if either is set. The owner of the repository.
            - RepositoryName: Do not set if RepositoryId is set. This and RepositoryName must both be set if either is set. The actual name of the repository
            - RepositoryId: Do not set if both RepositoryOwner and RepositoryName are set. The Id of the repository.
            - AccessToken: In case of a private repository, a GitHub API Access Token with permissions to access the repository is required.
            - TargetFile: The name of the file this tool is looking for. No other file will be downloaded.
            - ZipsToSubdirectory: 'true' if each zip file should be decompressed to a sub directory with the same name as the zip file. 'false' if all zip files are decompressed to the target directory directly.
            - DecompressZipFiles: 'true' if zip files should be decompressed. If 'false' no zip files at all will be decompressed.
        """;

    private const string FolderType = "application/vnd.google-apps.folder";
    private readonly ConcurrentDictionary<string, string> FolderIdCache = new();

    private readonly DriveService Drive;
    private readonly static RecyclableMemoryStreamManager MemoryStreamManager
        = new(
              71_680, // 71 KB, about 14 KB from being allocated into the LOH
              104_857_600 // 100 MB for large files, anything larger than that should be handled differently. In fact, it should be handled differently way before this point
          );
    private static RecyclableMemoryStream GetStream()
        => new(MemoryStreamManager, Guid.NewGuid());

    public GoogleDriveProvider(string? root, string apiKey)
    {
    }

    public void WriteData(string path, FileMode mode, ReadOnlySpan<byte> data)
    {
        using var mem = GetStream();
        mem.Write(data);
        WriteData(path, mode, mem);
    }

    public void WriteData(string path, FileMode mode, IEnumerable<byte> data)
    {
        var stream = new IEnumerableStream(data);
        WriteData(path, mode, stream);
    }

    public void WriteData(string path, FileMode mode, Stream data)
    {
        FilesResource.ListRequest listr;
        GFile? file = null;
        switch (mode)
        {
            case FileMode.CreateNew:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if (listr.Execute().Files.Any(x => x.Name == path))
                    throw new InvalidOperationException("The file already exists on Google Drive and cannot be created new");
                break;
            case FileMode.Truncate:
            case FileMode.Create:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if (listr.Execute().Files.FirstOrDefault(x => x.Name == path) is GFile cf)
                    Drive.Files.Delete(cf.Id).Execute();
                break;
            case FileMode.Append:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if (listr.Execute().Files.FirstOrDefault(x => x.Name == path) is GFile af)
                {
                    file = af;
                    var mem = new MemoryStream();
                    Drive.Files.Get(af.Id).Download(mem);

                    data = new ConcatenatedStream(mem, data);
                    Drive.Files.Delete(af.Id).Execute();
                }
                break;

            case FileMode.Open:
            case FileMode.OpenOrCreate:
                throw new ArgumentException($"Cannot use {mode} for write operations", nameof(mode));
            default:
                throw new ArgumentException($"Unknown FileMode {mode}", nameof(mode));
        }

        file ??= new GFile()
        {
            Name = PreparePath(path)
        };

        var filereq = Drive.Files.Create(
            file,
            data,
            ""
        );

        filereq.Upload();
    }

    public async Task WriteDataAsync(string path, FileMode mode, Stream data, CancellationToken ct = default)
    {
        FilesResource.ListRequest listr;
        GFile? file = null;
        switch (mode)
        {
            case FileMode.CreateNew:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if ((await listr.ExecuteAsync(ct)).Files.Any(x => x.Name == path))
                    throw new InvalidOperationException("The file already exists on Google Drive and cannot be created new");
                break;
            case FileMode.Truncate:
            case FileMode.Create:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if ((await listr.ExecuteAsync(ct)).Files.FirstOrDefault(x => x.Name == path) is GFile cf)
                    await Drive.Files.Delete(cf.Id).ExecuteAsync(ct);
                break;
            case FileMode.Append:
                listr = Drive.Files.List();
                listr.Q = $"name='{path}'";
                if ((await listr.ExecuteAsync(ct)).Files.FirstOrDefault(x => x.Name == path) is GFile af)
                {
                    file = af;
                    var mem = new MemoryStream();
                    await Drive.Files.Get(af.Id).DownloadAsync(mem, ct);

                    data = new ConcatenatedStream(mem, data);
                    await Drive.Files.Delete(af.Id).ExecuteAsync(ct);
                }
                break;

            case FileMode.Open:
            case FileMode.OpenOrCreate:
                throw new ArgumentException($"Cannot use {mode} for write operations", nameof(mode));
            default:
                throw new ArgumentException($"Unknown FileMode {mode}", nameof(mode));
        }

        file ??= new GFile()
        {
            Name = PreparePath(path)
        };

        var filereq = Drive.Files.Create(
            file,
            data,
            ""
        );

        await filereq.UploadAsync(ct);
    }

    public Task WriteDataAsync(string path, FileMode mode, IEnumerable<byte> data, CancellationToken ct = default)
    {
        var stream = new IEnumerableStream(data);
        return WriteDataAsync(path, mode, stream, ct);
    }

    public Task WriteDataAsync(string path, FileMode mode, byte[] data, CancellationToken ct = default)
    {
        return WriteDataAsync(path, mode, new MemoryStream(data), ct);
    }

    public DisposalManager<Stream> GetReadStream(string path)
    {
        var mem = GetStream();
        var f = GetFiles($"name='{path}'").FirstOrDefault() ?? throw new FileNotFoundException($"Could not find the file in the client's Google Drive");
        Drive.Files.Get(f.Id).Download(mem);
        return new DisposalManager<Stream>(mem, true);
    }

    public async ValueTask<DisposalManager<Stream>> GetReadStreamAsync(string path, CancellationToken ct = default)
    {
        var mem = GetStream();
        var f = GetFiles($"name='{path}'").FirstOrDefault() ?? throw new FileNotFoundException($"Could not find the file in the client's Google Drive");
        await Drive.Files.Get(f.Id).DownloadAsync(mem, ct);
        return new DisposalManager<Stream>(mem, true);
    }

    public DisposalManager<Stream> GetWriteStream(string path, FileMode mode)
    {
#warning Buffer it and then upload
        throw new NotSupportedException("Obtaining Write Streams is not supported by GoogleDriveProvider");
    }

    public ValueTask<DisposalManager<Stream>> GetWriteStreamAsync(string path, FileMode mode, CancellationToken ct = default)
    {
        throw new NotSupportedException("Obtaining Write Streams is not supported by GoogleDriveProvider");
    }

    public DisposalManager<Stream> GetReadWriteStream(string path, FileMode mode)
    {
#warning Buffer it, stage changes and upload
        throw new NotSupportedException("Obtaining Write Streams is not supported by GoogleDriveProvider");
    }

    public ValueTask<DisposalManager<Stream>> GetReadWriteStreamAsync(string path, FileMode mode, CancellationToken ct = default)
    {
        throw new NotSupportedException("Obtaining Streams is not supported by GoogleDriveProvider");
    }

    public bool DeleteDirectory(string path, bool recursive = false)
    {
    }

    public ValueTask<bool> DeleteDirectoryAsync(string path, bool recursive = false)
    {
    }

    public bool CreateDirectory(string path)
    {
        var components = DirectorySplitRegex.Split(path);
        bool created = false;
        string? previousId = null;
        foreach (var folder in components)
        {
            var fq = Drive.Files.List();
            fq.Q = previousId is not null ? $"name='{folder}' '{previousId}' in parents" : $"name='{folder}'";
            previousId = fq.Execute().Files.FirstOrDefault(x => x.MimeType == FolderType)?.Id;
            if (previousId is null)
            {
                var file = Drive.Files.Create(new GFile()
                {
                    Name = folder,
                    MimeType = FolderType
                }).Execute();
                previousId = file.Id;
            }
        }
        return created;
    }

    public ValueTask<bool> CreateDirectoryAsync(string path)
    {

    }

    public bool DirectoryExists(string path)
    {
    }

    public ValueTask<bool> DirectoryExistsAsync(string path)
    {
    }

    public ValueTask<string> GetDirectoryNameAsync(string path)
    {
        throw new NotImplementedException();
    }

    public void MoveFile(string path, string newPath)
    {
        throw new NotImplementedException();
    }

    public ValueTask MoveFileAsync(string path, string newPath)
    {
        throw new NotImplementedException();
    }

    public bool DeleteFile(string path)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> DeleteFileAsync(string path)
    {
        throw new NotImplementedException();
    }

    public bool FileExists(string path)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> FileExistsAsync(string path)
    {
        throw new NotImplementedException();
    }

    public string PreparePath(string path)
    {
        throw new NotImplementedException();
    }

    private async Task<IList<GFile>> GetFilesAsync(string q, CancellationToken ct)
    {
        var fq = Drive.Files.List();
        fq.Q = q;
        return (await fq.ExecuteAsync(ct)).Files;
    }

    private IList<GFile> GetFiles(string q)
    {
        var fq = Drive.Files.List();
        fq.Q = q;
        return fq.Execute().Files;
    }

    private async Task<string?> GetFolderIdAsync(string path, CancellationToken ct)
    {
        var components = DirectorySplitRegex.Split(path);
        string? previous = null;
        foreach (var folder in components)
        {
            var fq = Drive.Files.List();
            fq.Q = previous is not null ? $"name='{folder}' '{previous}' in parents" : $"name='{folder}'";
            previous = (await fq.ExecuteAsync(ct)).Files.FirstOrDefault(x => x.MimeType == FolderType)?.Id;
            if (previous is null)
                return null;
        }
        return previous;
    }

    private string? GetFolderId(string path)
    {
        var components = DirectorySplitRegex.Split(path);
        string? previous = null;
        foreach (var folder in components)
        {
            var fq = Drive.Files.List();
            fq.Q = previous is not null ? $"name='{folder}' '{previous}' in parents" : $"name='{folder}'";
            previous = fq.Execute().Files.FirstOrDefault(x => x.MimeType == FolderType)?.Id;
            if (previous is null)
                return null;
        }
        return previous;
    }
}
