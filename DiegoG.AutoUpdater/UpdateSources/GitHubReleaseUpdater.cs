using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Octokit;
using Serilog;

namespace DiegoG.AutoUpdater.UpdateSources;

public record class GitHubReleaseUpdaterOptions(string? RepositoryOwner, string? RepositoryName, long? RepositoryId, string? AccessToken, string? TargetFile, bool ZipsToSubdirectory, bool DecompressZipFiles);

[UpdateSource("github-release")]
public class GitHubReleaseUpdater : IUpdateSource
{
    private GitHubClient? Client;
    private GitHubReleaseUpdaterOptions? Options;

    public void UseOptions(JsonDocument options)
    {
        Options = JsonSerializer.Deserialize<GitHubReleaseUpdaterOptions>(options)!;
        Client = new(new ProductHeaderValue("DiegoG.AutoUpdater.GitHubReleaseUpdater", "0.0.1"));
        if (Options.AccessToken is not null)
            Client.Credentials = new Credentials(Options.AccessToken);

        if (
            (Options.RepositoryId is null &&
            (string.IsNullOrWhiteSpace(Options.RepositoryOwner) || 
             string.IsNullOrWhiteSpace(Options.RepositoryName)))
        )
            throw new ArgumentException("Invalid Options. RepositoryId cannot be null if either RepositoryName or RepositoryOwner is also null. AccessToken cannot be null.");
    }

    [MemberNotNull(nameof(Client), nameof(Options))]
    private void ThrowIfNotConfigured()
    {
        if (Client is null || Options is null || Client.Credentials is null)
            throw new InvalidOperationException("This UpdateSource has not been properly configured");
    }

    public async Task<bool> CheckUpdate(VersionHash hash)
    {
        ThrowIfNotConfigured();
        IReadOnlyList<Release> releases;
        if (Options.RepositoryId is long id)
            releases = await Client.Repository.Release.GetAll(id);
        else
        {
            Debug.Assert(Options.RepositoryOwner is not null, "RepositoryOwner is unexpectedly null");
            Debug.Assert(Options.RepositoryName is not null, "RepositoryName is unexpectedly null");
            releases = await Client.Repository.Release.GetAll(Options.RepositoryOwner, Options.RepositoryName);
        }

        var r = releases.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        if (r is null)
            return false;

        var newhash = VersionHash.Create(r.TagName);
        return newhash != hash;
    }

    public async Task<VersionHash?> Update(ILogger log, DirectoryInfo target)
    {
        ThrowIfNotConfigured();

        log.Information("Obtaining repository info from GitHub");
        IReadOnlyList<Release> releases;
        if (Options.RepositoryId is long id)
            releases = await Client.Repository.Release.GetAll(id);
        else
        {
            Debug.Assert(Options.RepositoryOwner is not null, "RepositoryOwner is unexpectedly null");
            Debug.Assert(Options.RepositoryName is not null, "RepositoryName is unexpectedly null");
            releases = await Client.Repository.Release.GetAll(Options.RepositoryOwner, Options.RepositoryName);
        }

        var r = releases.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (r is null)
        {
            log.Warning("Failed to fetch information from GitHub");
            return default;
        }

        log.Debug("Preparing HTTP Client for downloading");
        var http = new HttpClient();

        if (Options.TargetFile is not string targetFile)
        {
            log.Information("Downloading all release assets from repository");

            HashSet<(string TempFile, ReleaseAsset Asset)> pendingMoves = new();

            foreach (var asset in r.Assets)
            {
                var file = Path.GetTempFileName();
                pendingMoves.Add((file, asset));
                using var fs = File.Create(file);
                var hmsg = await http.GetAsync(asset.BrowserDownloadUrl);
                await hmsg.Content.CopyToAsync(fs);

                if (Options.DecompressZipFiles)
                    if (asset.Name.EndsWith(".zip"))
                    {
                        var finaldir = Options.ZipsToSubdirectory ? Path.Combine(target.FullName, Path.GetFileNameWithoutExtension(asset.Name)) : target.FullName;
                        Directory.CreateDirectory(finaldir);

                        log.Debug("Decompressing file {file} into target {dir}", file, finaldir);
                        using var zip = new ZipArchive(fs);
                        zip.ExtractToDirectory(finaldir, true);

                        pendingMoves.Remove((file, asset));
                    }
            }

            log.Information("Moving all non-decompressed asset files");
            foreach (var (file, asset) in pendingMoves)
            {
                var final = Path.Combine(target.FullName, asset.Name);
                log.Debug("Moving {file} to {final}", file, final);
                File.Move(file, final, true);
            }
        }
        else 
        {
            log.Information("Attempting to download asset file '{file}'", targetFile);
            var asset = r.Assets.FirstOrDefault(x => x.Name == targetFile);

            if (asset is null)
            {
                log.Error("Could not find asset file '{file}'", targetFile);
                return null;
            }

            log.Debug("Found asset file '{file}', Downloading", targetFile);

            var file = Path.GetTempFileName();
            using var fs = File.Create(file);
            var hmsg = await http.GetAsync(asset.BrowserDownloadUrl);
            await hmsg.Content.CopyToAsync(fs);

            if (Options.DecompressZipFiles)
                if (asset.Name.EndsWith(".zip"))
                {
                    var finaldir = Options.ZipsToSubdirectory ? Path.Combine(target.FullName, Path.GetFileNameWithoutExtension(asset.Name)) : target.FullName;
                    Directory.CreateDirectory(finaldir);

                    log.Debug("Decompressing file {file} into target {dir}", file, finaldir);
                    using var zip = new ZipArchive(fs);
                    zip.ExtractToDirectory(finaldir, true);
                }
                else
                {
                    var final = Path.Combine(target.FullName, asset.Name);
                    log.Debug("Moving {file} to {final}", file, final);
                    File.Move(file, final, true);
                }
        }

        log.Information("Succesfully pulled all relevant assets from GitHub Repository {Owner}/{Repo} into {target}", Options.RepositoryOwner, Options.RepositoryName, target.FullName);

        log.Debug("Generating VersionHash from Release TagName");
        var hash = VersionHash.Create(r.TagName);
        log.Information("Generated VersionHash {hash}", hash.ToString());

        return hash;
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
}
