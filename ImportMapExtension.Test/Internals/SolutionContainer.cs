using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace ImportMapExtension.Test.Internals;

/// <summary>What a command that ran inside the container did.</summary>
internal sealed record CommandResult(long ExitCode, string Output);

/// <summary>
/// A disposable Linux container with a copy of this solution in it, for the tests that have to
/// build, publish or run the sample app.
/// <para>
/// The working tree is mounted read only and copied into the container, so nothing any of these
/// tests do can reach it. The NuGet packages in the "_dist" folder come along and become the feed
/// that the sample app restores from. Because the container has a NuGet global package cache of its
/// own, and a pristine one, the package that <see cref="PackageBuilder"/> just built is what every
/// one of these tests gets, and nothing they do can leave anything behind in the cache of the
/// machine running them.
/// </para>
/// </summary>
internal sealed class SolutionContainer : IAsyncDisposable
{
    /// <summary>
    /// The SDK to build with. This package hooks into the step of the .NET SDK that generates import
    /// maps, and 10.0.400 is the first SDK to have it.
    /// </summary>
    private const string SdkImage = "mcr.microsoft.com/dotnet/sdk:10.0.400";

    /// <summary>Where the copy of the solution that gets built lives inside the container.</summary>
    public const string SolutionDir = "/work";

    public const string SampleAppProject = $"{SolutionDir}/SampleApp/SampleApp.csproj";

    /// <summary>Where the sample app's development server listens inside the container.</summary>
    private const int DevServerPort = 8080;

    /// <summary>Where the working tree is mounted, read only, for the copy above to be made from.</summary>
    private const string SourceDir = "/src";

    /// <summary>Folder names that hold nothing a build reads, wherever in the tree they turn up.</summary>
    private static readonly string[] NotWorthCopying = ["bin", "obj", ".vs", ".git", "TestResults"];

    /// <summary>Where that copy is staged. It is deleted as soon as it has been unpacked.</summary>
    private const string ArchivePath = "/tmp/solution.tar";

    /// <summary>Announced once that copy is complete, so that nothing runs against half of it.</summary>
    private const string CopiedMessage = "the solution has been copied";

    /// <summary>The copy above, and for a development server a restore and a build, fit in this.</summary>
    private static readonly TimeSpan StartUpTimeout = TimeSpan.FromMinutes(10);

    private readonly IContainer _Container;

    private SolutionContainer(IContainer container)
    {
        this._Container = container;
    }

    /// <summary>The address that the development server of this container answers on.</summary>
    public string BaseAddress => $"http://{this._Container.Hostname}:{this._Container.GetMappedPublicPort(DevServerPort)}/";

    /// <summary>
    /// Hands back a container that mounts the working tree read only and takes a copy of it, minus
    /// everything a build produces, into a folder of its own before running <paramref name="command"/>.
    /// <para>
    /// The mount being read only is what puts the working tree out of reach of everything the
    /// container does. Building in a copy on the container's own file system rather than in the
    /// mount is both quicker and the only way to be sure that a root process in a container leaves
    /// nothing behind that the test run cannot delete afterwards.
    /// </para>
    /// <para>
    /// The copy goes through an archive on disk rather than through a pipe. "tar" reports a file it
    /// could not read and carries on to write a well formed archive, so the reading half of a pipe
    /// would unpack whatever did arrive and report success on a copy that is missing something.
    /// </para>
    /// </summary>
    private static ContainerBuilder Prepare(string command)
    {
        var excluded = string.Join(' ', NotWorthCopying.Select(name => $"--exclude={name}"));

        return new ContainerBuilder(SdkImage)
            .WithBindMount(PathUtils.SolutionDir, SourceDir, AccessMode.ReadOnly)
            .WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .WithEnvironment("DOTNET_NOLOGO", "1")
            .WithEntrypoint("sh", "-c",
                $"tar -C {SourceDir} {excluded} -cf {ArchivePath} . && " +
                // Docker creates a container's working directory for it before the entrypoint runs,
                // so this is here for the containers that ask for no working directory.
                $"mkdir -p {SolutionDir} && " +
                $"tar -C {SolutionDir} -xf {ArchivePath} --no-same-owner && " +
                $"rm {ArchivePath} && {command}");
    }

    /// <summary>
    /// Starts a container that sits and waits, for the tests that run a build or a publish in it and
    /// then read what came out.
    /// </summary>
    public static async Task<SolutionContainer> StartAsync()
    {
        var container = Prepare($"echo '{CopiedMessage}' && tail -f /dev/null")
            .WithWorkingDirectory(SolutionDir)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged(CopiedMessage, options => options.WithTimeout(StartUpTimeout)))
            .Build();

        await container.StartAsync();
        return new SolutionContainer(container);
    }

    /// <summary>
    /// Starts a container running the sample app's development server, the way "dotnet run" does on
    /// a developer's machine, and waits for it to listen.
    /// </summary>
    /// <param name="extraArguments">The MSBuild properties to build the sample app with.</param>
    public static async Task<SolutionContainer> StartDevServerAsync(string extraArguments = "")
    {
        var container = Prepare($"cd {SolutionDir}/SampleApp && exec dotnet run --no-launch-profile {extraArguments}")
            .WithEnvironment("ASPNETCORE_URLS", $"http://0.0.0.0:{DevServerPort}")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithPortBinding(DevServerPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Now listening on", options => options.WithTimeout(StartUpTimeout)))
            .Build();

        await container.StartAsync();
        return new SolutionContainer(container);
    }

    /// <summary>
    /// Runs "dotnet" against the copy of the solution in the container and hands back everything it
    /// wrote. <paramref name="allowFailure"/> is for the tests whose subject is a build that has to
    /// fail; for those, the output is the thing being checked.
    /// </summary>
    public async Task<CommandResult> DotNetAsync(string arguments, bool allowFailure = false)
    {
        var result = await this._Container.ExecAsync(["sh", "-c", $"cd {SolutionDir} && dotnet {arguments} 2>&1"]);
        var output = result.Stdout + result.Stderr;

        // Docker leaves this unset only when it could not run the command at all, which is not a
        // result any of these tests can say anything about.
        var exitCode = result.ExitCode ?? throw new InvalidOperationException(
            $"The container reported no exit code for \"dotnet {arguments}\".\n{output}");

        if (!allowFailure)
        {
            exitCode.Is(0L, message: $"\"dotnet {arguments}\" failed in the container (exit code {exitCode}).\n{output}");
        }
        return new CommandResult(exitCode, output);
    }

    /// <summary>The full paths of the files in the container that match the given shell pattern.</summary>
    public async Task<string[]> GlobAsync(string pattern)
    {
        var result = await this._Container.ExecAsync(["sh", "-c", $"ls -1d {pattern} 2>/dev/null"]);
        return [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>The full paths of the files anywhere under the given folder whose names match.</summary>
    public async Task<string[]> FindFilesAsync(string directory, string namePattern)
    {
        var result = await this._Container.ExecAsync(["find", directory, "-type", "f", "-name", namePattern]);
        return [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public async Task<bool> FileExistsAsync(string path) => (await this._Container.ExecAsync(["test", "-f", path])).ExitCode == 0;

    public Task<byte[]> ReadBytesAsync(string path) => this._Container.ReadFileAsync(path);

    /// <summary>
    /// Reads a text file out of the container, the way "File.ReadAllTextAsync" would read it here:
    /// a byte order mark says what the encoding is and is not part of what comes back.
    /// </summary>
    public async Task<string> ReadTextAsync(string path)
    {
        using var content = new MemoryStream(await this.ReadBytesAsync(path));
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    public Task WriteTextAsync(string path, string content) => this._Container.CopyAsync(Encoding.UTF8.GetBytes(content), path);

    /// <summary>Everything the container wrote to its console, for a test that has to explain itself.</summary>
    public async Task<string> ReadConsoleAsync()
    {
        var (stdout, stderr) = await this._Container.GetLogsAsync();
        return stdout + stderr;
    }

    public ValueTask DisposeAsync() => this._Container.DisposeAsync();
}
