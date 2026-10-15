using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Dev;

/// <summary>
/// Runs the unit, integration and browser suites (story E0-06). A missing project, a failed run or zero executed
/// tests is a failure, never a pass. The browser suite runs against an isolated Compose project (own name, ports
/// and volumes) so it never touches the developer's stack; the runner removes only what it started.
/// </summary>
public static class TestCommand
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private sealed record Suite(string Name, string Project);

    private sealed record SuiteResult(string Name, string Status, int Discovered, int Executed, int Passed, int Failed, int Skipped, string Note);

    private static readonly Suite[] Suites =
    [
        new("unit", "tests/EventPermits.UnitTests"),
        new("integration", "tests/EventPermits.IntegrationTests"),
        new("browser", "tests/EventPermits.BrowserTests"),
    ];

    public static async Task<int> RunAsync(Options o)
    {
        var which = (o.Value("--suite") ?? "all").ToLowerInvariant();
        if (which is not ("unit" or "integration" or "browser" or "all"))
            throw new DevException("--suite must be unit, integration, browser or all.");
        var selected = Suites.Where(s => which == "all" || s.Name == which).ToArray();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var root = Repo.Path("TestResults", stamp);
        Directory.CreateDirectory(root);
        Out.Step($"Test run {stamp}: {string.Join(", ", selected.Select(s => s.Name))}");
        Out.Info($"Results: {root}");

        var results = new List<SuiteResult>();
        foreach (var suite in selected)
        {
            Out.Step($"Suite: {suite.Name} ({suite.Project})");
            if (!Directory.Exists(Repo.Path(suite.Project)))
            {
                Out.Fail($"suite missing: {suite.Project} does not exist");
                results.Add(new(suite.Name, "MISSING", 0, 0, 0, 0, 0, $"{suite.Project} does not exist"));
                continue;
            }
            var dir = Path.Combine(root, suite.Name);
            Directory.CreateDirectory(dir);
            results.Add(suite.Name == "browser"
                ? await RunBrowserAsync(suite, dir, o)
                : await RunProjectAsync(suite, dir, new Dictionary<string, string>(), o.PassThrough));
        }

        Out.Step("Summary");
        var header = $"  {"suite",-12} {"status",-8} {"discovered",10} {"executed",9} {"passed",7} {"failed",7} {"skipped",8}";
        Console.WriteLine(header);
        foreach (var r in results)
            Console.WriteLine($"  {r.Name,-12} {r.Status,-8} {r.Discovered,10} {r.Executed,9} {r.Passed,7} {r.Failed,7} {r.Skipped,8}{(r.Note.Length > 0 ? "  " + r.Note : "")}");

        var report = Path.Combine(root, "report.md");
        await File.WriteAllTextAsync(report, await MarkdownAsync(stamp, results, o));
        Out.Info($"Report: {report}");
        var ok = results.Count > 0 && results.All(r => r.Status == "PASSED");
        Out.Step(ok ? "Tests PASSED" : "Tests FAILED (a suite failed, is missing, or executed 0 tests)");
        return ok ? 0 : 1;
    }

    /// <summary>`screenshots`: the browser project filtered to trait Category=Screenshots against a running stack.</summary>
    public static async Task<int> ScreenshotsAsync(Options o)
    {
        var suite = Suites.Single(s => s.Name == "browser");
        if (!Directory.Exists(Repo.Path(suite.Project))) return Commands.Fail($"suite missing: {suite.Project} does not exist.");
        var baseUrl = (o.Value("--url") ?? Commands.LocalBaseUrl).TrimEnd('/');
        Out.Step($"Screenshots against {baseUrl}");
        if (!await Proc.WaitHttpAsync($"{baseUrl}/health/ready", TimeSpan.FromSeconds(30)))
            return Commands.Fail($"{baseUrl} is not ready. Start the stack first: dotnet run --project tools/Dev -- up");
        var dir = Repo.Path("TestResults", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), "screenshots");
        Directory.CreateDirectory(dir);
        var r = await RunProjectAsync(suite, dir, new Dictionary<string, string> { ["E2E__BaseUrl"] = baseUrl },
            ["--filter-trait", "Category=Screenshots", .. o.PassThrough]);
        Out.Info($"Screenshots are written by the tests to {Repo.Path("docs", "screenshots")}; TRX in {dir}");
        Out.Info($"Executed {r.Executed}, passed {r.Passed}, failed {r.Failed}, skipped {r.Skipped}.");
        return r.Status == "PASSED" ? 0 : 1;
    }

    private static async Task<SuiteResult> RunBrowserAsync(Suite suite, string dir, Options o)
    {
        var url = o.Value("--url");
        if (url is not null)
        {
            Out.Info($"Using the given instance {url} (no isolated stack started).");
            return await RunProjectAsync(suite, dir, new Dictionary<string, string> { ["E2E__BaseUrl"] = url.TrimEnd('/') }, o.PassThrough);
        }

        var env = new Dictionary<string, string>
        {
            ["WEB_PORT"] = Repo.TestWebPort.ToString(CultureInfo.InvariantCulture),
            ["STORAGE_PORT"] = Repo.TestStoragePort.ToString(CultureInfo.InvariantCulture),
        };
        string[] compose = ["compose", "-p", Repo.TestComposeProject, "-f", Repo.ComposeFile];
        Out.Info($"Starting isolated stack: project {Repo.TestComposeProject}, web 127.0.0.1:{Repo.TestWebPort}, storage 127.0.0.1:{Repo.TestStoragePort}");
        SuiteResult result;
        var up = await Proc.RunAsync("docker", [.. compose, "up", "-d", "--wait", "--build"], env);
        if (!up.Ok)
        {
            Out.Fail("The isolated test stack did not become healthy.");
            result = new(suite.Name, "FAILED", 0, 0, 0, 0, 0, "isolated stack did not start");
        }
        else
        {
            result = await RunProjectAsync(suite, dir,
                new Dictionary<string, string> { ["E2E__BaseUrl"] = $"http://127.0.0.1:{Repo.TestWebPort}" }, o.PassThrough);
        }

        if (o.Flag("--keep-stack"))
            Out.Info($"--keep-stack: left running. Remove later with: docker compose -p {Repo.TestComposeProject} down --volumes");
        else
        {
            Out.Info($"Removing the isolated stack and its own volumes (project {Repo.TestComposeProject} only).");
            var down = await Proc.RunAsync("docker", [.. compose, "down", "--volumes"], env);
            if (!down.Ok) Out.Warn($"Cleanup failed; run: docker compose -p {Repo.TestComposeProject} down --volumes");
        }
        return result;
    }

    private static async Task<SuiteResult> RunProjectAsync(Suite suite, string dir, Dictionary<string, string> env, IReadOnlyList<string> extra)
    {
        List<string> args = ["test", "--project", suite.Project, "--report-trx", "--results-directory", dir];
        if (extra.Count > 0) args.AddRange(["--", .. extra]);
        var run = await Proc.RunAsync("dotnet", args, env);
        var trx = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.trx", SearchOption.AllDirectories) : [];
        if (trx.Length == 0)
        {
            Out.Fail($"No TRX file produced in {dir} (exit code {run.ExitCode}).");
            return new(suite.Name, "FAILED", 0, 0, 0, 0, 0, "no TRX results");
        }
        int discovered = 0, executed = 0, passed = 0, failed = 0, skipped = 0;
        foreach (var file in trx)
        {
            var counters = XDocument.Load(file).Root?.Element(Trx + "ResultSummary")?.Element(Trx + "Counters");
            if (counters is null) continue;
            int A(string n) => int.TryParse(counters.Attribute(n)?.Value, out var v) ? v : 0;
            var total = A("total");
            var exec = A("executed");
            discovered += total;
            executed += exec;
            passed += A("passed");
            failed += A("failed") + A("error") + A("timeout") + A("aborted");
            skipped += counters.Attribute("notExecuted") is not null ? A("notExecuted") : Math.Max(0, total - exec);
        }
        var status = run.ExitCode != 0 || failed > 0 ? "FAILED" : executed == 0 ? "NO TESTS" : "PASSED";
        var note = status == "FAILED" && failed == 0 ? $"dotnet test exit code {run.ExitCode}" : status == "NO TESTS" ? "0 tests executed" : "";
        return new(suite.Name, status, discovered, executed, passed, failed, skipped, note);
    }

    private static async Task<string> MarkdownAsync(string stamp, List<SuiteResult> results, Options o)
    {
        var commit = (await Proc.RunAsync("git", ["rev-parse", "--short", "HEAD"], echo: false)).Output.Trim();
        var dirty = (await Proc.RunAsync("git", ["status", "--porcelain"], echo: false)).Output.Trim().Length > 0;
        var sb = new StringBuilder();
        sb.AppendLine($"# Test report {stamp}").AppendLine();
        sb.AppendLine($"- UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Commit: {(commit.Length > 0 ? commit : "unknown")}{(dirty ? " (working tree has uncommitted changes)" : "")}");
        sb.AppendLine($"- OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"- .NET runtime: {Environment.Version}");
        sb.AppendLine($"- Browser suite target: {o.Value("--url") ?? $"isolated Compose project {Repo.TestComposeProject} on 127.0.0.1:{Repo.TestWebPort}"}");
        if (o.PassThrough.Count > 0) sb.AppendLine($"- Extra test arguments: `{string.Join(' ', o.PassThrough)}`");
        sb.AppendLine().AppendLine("| Suite | Status | Discovered | Executed | Passed | Failed | Skipped | Note |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var r in results)
            sb.AppendLine($"| {r.Name} | {r.Status} | {r.Discovered} | {r.Executed} | {r.Passed} | {r.Failed} | {r.Skipped} | {r.Note} |");
        sb.AppendLine().AppendLine(results.All(r => r.Status == "PASSED")
            ? "Overall: **PASSED**"
            : "Overall: **FAILED** (a suite failed, is missing, or executed 0 tests)");
        return sb.ToString();
    }
}
