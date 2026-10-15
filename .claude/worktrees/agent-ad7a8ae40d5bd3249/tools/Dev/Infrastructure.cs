using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Dev;

/// <summary>Repository-relative paths, resolved by walking up to EventPermits.slnx (never from the shell's cwd alone).</summary>
public static class Repo
{
    public const string SolutionFile = "EventPermits.slnx";
    public const int WebPort = 5280;
    public const int StoragePort = 9000;
    public const string ComposeProject = "event-permits";
    public const string TestComposeProject = "event-permits-test";
    public const int TestWebPort = 5380;
    public const int TestStoragePort = 9300;
    public const string DemoPassword = "EventPermits!Demo2026";

    private static readonly Lazy<string> RootLazy = new(FindRoot);
    public static string Root => RootLazy.Value;
    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
    public static string ComposeFile => Path("compose.yml");

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(System.IO.Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
        }
        throw new DevException($"Run this command from inside the repository ({SolutionFile} not found above the current directory).");
    }
}

/// <summary>An expected failure with an actionable message; printed without a stack trace.</summary>
public sealed class DevException(string message) : Exception(message);

/// <summary>Command-line options: `--name value`, boolean `--flag`, and everything after a standalone `--` passed through.</summary>
public sealed class Options
{
    private static readonly HashSet<string> ValueOptions = ["--suite", "--url", "--credentials-file"];
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public IReadOnlyList<string> PassThrough { get; private set; } = [];

    public string? Value(string name) => _values.GetValueOrDefault(name);
    public bool Flag(string name) => _flags.Contains(name);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var pass = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { pass.AddRange(args[(i + 1)..]); break; }
            if (ValueOptions.Contains(a))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new DevException($"{a} needs a value.");
                o._values[a] = args[++i];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal)) o._flags.Add(a);
            else throw new DevException($"Unexpected argument '{a}'. Run: dotnet run --project tools/Dev -- help");
        }
        o.PassThrough = pass;
        return o;
    }
}

public sealed record ProcResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs child processes with ArgumentList (never one concatenated string, never through a shell).</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, IDictionary<string, string>? env = null,
        bool echo = true, TimeSpan? timeout = null, Func<string, bool>? filter = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new DevException($"Could not start {file}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcResult(127, $"{file} is not installed or not on PATH.");
        }
        using var _ = process;
        var output = new StringBuilder();
        void Handle(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            if (echo && (filter is null || filter(line))) Console.WriteLine("    " + line);
        }
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        using var cts = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ProcResult(124, output + $"\nTimed out after {limit.TotalMinutes:0} minutes.");
        }
        return new ProcResult(process.ExitCode, output.ToString());
    }

    public static Task<ProcResult> Dotnet(params string[] args) => RunAsync("dotnet", args);

    /// <summary>docker compose for the developer's stack (project event-permits, compose.yml at the repo root).</summary>
    public static Task<ProcResult> Compose(IEnumerable<string> args, IDictionary<string, string>? env = null, bool echo = true) =>
        RunAsync("docker", ["compose", "-f", Repo.ComposeFile, .. args], env, echo);

    public static bool PortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitHttpAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var r = await http.GetAsync(url);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(1000);
        }
        return false;
    }
}

public static class Out
{
    public static void Step(string text) => Console.WriteLine($"\n== {text}");
    public static void Ok(string text) => Console.WriteLine($"  [ok]   {text}");
    public static void Warn(string text) => Console.WriteLine($"  [warn] {text}");
    public static void Fail(string text) => Console.WriteLine($"  [FAIL] {text}");
    public static void Info(string text) => Console.WriteLine($"  {text}");
}
