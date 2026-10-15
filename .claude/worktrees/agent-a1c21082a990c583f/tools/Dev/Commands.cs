using System.Text.Json;

namespace Dev;

public static class Commands
{
    public static readonly (string User, string[] Roles)[] DemoAccounts =
    [
        ("intake.alpha", ["IntakeCoordinator"]),
        ("intake.bravo", ["IntakeCoordinator"]),
        ("reviewer.charlie", ["EligibilityReviewer"]),
        ("scheduler.delta", ["SchedulingOfficer"]),
        ("manager.echo", ["OperationsManager"]),
        ("attendant.foxtrot", ["EventAttendant"]),
        ("attendant.golf", ["EventAttendant"]),
        ("auditor.hotel", ["ComplianceAuditor"]),
        ("admin.india", ["SystemAdmin"]),
        ("dual.juliet", ["EligibilityReviewer", "OperationsManager"]),
    ];

    public static int Help(int exitCode, string? error = null)
    {
        if (error is not null) Out.Fail(error);
        Console.WriteLine("""
            Usage: dotnet run --project tools/Dev -- <command> [options]

              doctor                       check .NET SDK, Docker, Compose v2, Git and ports 5280/9000
              setup                        doctor, dotnet tool restore, locked restore, Playwright Chromium (if built)
              build                        locked restore and Debug build of EventPermits.slnx
              up                           docker compose up -d --wait --build; prints URLs and demo accounts
              down                         docker compose down (data is kept; never runs --volumes)
              migrate                      apply migrations only (no seed) through the Compose stack
              seed                         idempotent seed through the Compose stack
              seed --reset --yes           DESTRUCTIVE: wipe and reseed the local database
              test [--suite unit|integration|browser|all] [--keep-stack] [--url BASE] [-- <test args>]
                                           run suites; browser suite uses an isolated stack (project event-permits-test,
                                           ports 5380/9300) unless --url is given; results in TestResults/<UTC>/
              smoke [--url BASE] [--credentials-file PATH] [--remote] [--yes]
                                           HTTP smoke: health, landing, anonymous access, sign-in per role, 26 pages;
                                           --yes adds the web restart-persistence check (local only)
              scan                         Gitleaks secret scan (Git history and working tree) via Docker
              screenshots [--url BASE]     browser tests with trait Category=Screenshots (writes docs/screenshots/)
              help                         this list
            """);
        return exitCode;
    }

    public static async Task<int> DoctorAsync()
    {
        var ok = true;
        Out.Step("Repository");
        Out.Ok(Repo.Root);

        Out.Step(".NET SDK");
        var required = JsonDocument.Parse(File.ReadAllText(Repo.Path("global.json"))).RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        var sdks = await Proc.RunAsync("dotnet", ["--list-sdks"], echo: false);
        var installed = sdks.Output.Split('\n').Select(l => l.Split(' ')[0].Trim()).Where(v => v.Length > 0).ToList();
        var selected = (await Proc.RunAsync("dotnet", ["--version"], echo: false)).Output.Trim();
        if (installed.Any(v => SatisfiesLatestPatch(v, required)) && SatisfiesLatestPatch(selected, required))
            Out.Ok($"dotnet --version {selected} satisfies global.json {required} (rollForward latestPatch)");
        else
        {
            ok = false;
            Out.Fail($"global.json requires SDK {required} or a later patch in the same feature band; dotnet --version is '{selected}', installed: {string.Join(", ", installed)}. Install it from https://dotnet.microsoft.com/download/dotnet/10.0");
        }

        Out.Step("Docker");
        var docker = await Proc.RunAsync("docker", ["version", "--format", "{{.Client.Version}}|{{.Server.Version}}"], echo: false);
        var parts = docker.Output.Trim().Split('|');
        if (docker.Ok && parts.Length == 2 && parts[1].Length > 0) Out.Ok($"Docker client {parts[0]}, engine {parts[1]}");
        else
        {
            ok = false;
            Out.Fail(docker.ExitCode == 127 ? "Docker is not installed. Install Docker Desktop (or Docker Engine) and start it." : "Docker engine is not reachable. Start Docker Desktop and retry.");
        }
        var compose = await Proc.RunAsync("docker", ["compose", "version", "--short"], echo: false);
        var composeVersion = compose.Output.Trim().TrimStart('v');
        if (compose.Ok && int.TryParse(composeVersion.Split('.')[0], out var major) && major >= 2) Out.Ok($"Docker Compose {composeVersion} (Compose v2+ plugin)");
        else { ok = false; Out.Fail("Docker Compose v2 (the `docker compose` plugin) is not available. Update Docker Desktop or install the compose plugin."); }

        Out.Step("Git");
        var git = await Proc.RunAsync("git", ["--version"], echo: false);
        if (git.Ok) Out.Ok(git.Output.Trim());
        else { ok = false; Out.Fail("Git is not installed. Install it from https://git-scm.com/downloads"); }

        Out.Step("Ports");
        var ours = docker.Ok ? (await Proc.RunAsync("docker", ["ps", "--filter", $"label=com.docker.compose.project={Repo.ComposeProject}", "--format", "{{.Ports}}"], echo: false)).Output : "";
        foreach (var (port, use) in new[] { (EnvPort("WEB_PORT", Repo.WebPort), "web (WEB_PORT)"), (EnvPort("STORAGE_PORT", Repo.StoragePort), "S3 storage API (STORAGE_PORT)") })
        {
            if (Proc.PortFree(port)) Out.Ok($"{port} free for {use}");
            else if (ours.Contains($":{port}->", StringComparison.Ordinal)) Out.Ok($"{port} in use by this project's running stack ({Repo.ComposeProject}) for {use}");
            else { ok = false; Out.Fail($"{port} is in use by another process. Stop it, or set {use.Split('(')[1].TrimEnd(')')} to a free port before `up`."); }
        }

        Out.Step(ok ? "Doctor: all required tools present" : "Doctor: fix the [FAIL] items above");
        return ok ? 0 : 1;
    }

    public static int EnvPort(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var p) ? p : fallback;

    public static string LocalBaseUrl => $"http://127.0.0.1:{EnvPort("WEB_PORT", Repo.WebPort)}";

    /// <summary>global.json rollForward latestPatch: same major.minor and feature band (hundreds of the patch), patch at least the pinned one.</summary>
    private static bool SatisfiesLatestPatch(string candidate, string required)
    {
        static int[]? Parse(string v)
        {
            var core = v.Split('-')[0].Split('.');
            return core.Length == 3 && core.All(c => int.TryParse(c, out _)) ? core.Select(int.Parse).ToArray() : null;
        }
        if (candidate.Contains('-')) return false; // previews never satisfy a release pin
        var c = Parse(candidate);
        var r = Parse(required);
        return c is not null && r is not null && c[0] == r[0] && c[1] == r[1] && c[2] / 100 == r[2] / 100 && c[2] >= r[2];
    }

    public static async Task<int> SetupAsync()
    {
        if (await DoctorAsync() != 0) return Fail("Setup stopped: doctor found missing prerequisites.");

        Out.Step("Local .NET tools (dotnet tool restore)");
        if (!(await Proc.Dotnet("tool", "restore")).Ok) return Fail("dotnet tool restore failed.");

        Out.Step("Locked package restore (EventPermits.slnx)");
        if (!(await Proc.Dotnet("restore", Repo.SolutionFile, "--locked-mode")).Ok)
            return Fail("Locked restore failed. packages.lock.json files must match Directory.Packages.props.");

        Out.Step("Playwright Chromium for the browser tests");
        var browserProject = Repo.Path("tests", "EventPermits.BrowserTests");
        if (!Directory.Exists(browserProject))
        {
            Out.Warn("Browser setup skipped: tests/EventPermits.BrowserTests does not exist yet.");
            return 0;
        }
        if (!(await Proc.Dotnet("build", "tests/EventPermits.BrowserTests", "-c", "Debug", "-p:LockedRestore=true")).Ok)
            return Fail("Building tests/EventPermits.BrowserTests failed; Chromium not installed.");
        var playwrightDll = Path.Combine(browserProject, "bin", "Debug", "net10.0", "Microsoft.Playwright.dll");
        if (!File.Exists(playwrightDll))
        {
            Out.Warn($"Browser setup skipped: {Path.GetRelativePath(Repo.Root, playwrightDll)} not found (does the project reference Microsoft.Playwright?).");
            return 0;
        }
        if (!(await Proc.Dotnet(playwrightDll, "install", "chromium")).Ok) return Fail("Playwright Chromium install failed.");
        Out.Ok("Chromium installed (version pinned by the Microsoft.Playwright package).");
        return 0;
    }

    public static async Task<int> BuildAsync()
    {
        Out.Step("Build EventPermits.slnx (Debug, locked restore)");
        var build = await Proc.RunAsync("dotnet", ["build", Repo.SolutionFile, "-c", "Debug", "-p:LockedRestore=true"],
            filter: l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("warn", StringComparison.OrdinalIgnoreCase) || l.Contains("Build succeeded") || l.Contains("Elapsed"));
        return build.Ok ? Done("Build succeeded.") : Fail("Build failed (see errors above).");
    }

    public static async Task<int> UpAsync()
    {
        Out.Step("Start the local stack (docker compose up -d --wait --build)");
        if (!(await Proc.Compose(["up", "-d", "--wait", "--build"])).Ok)
            return Fail("The stack did not become healthy. Inspect with: docker compose ps  and  docker compose logs web migrate");
        var b = LocalBaseUrl;
        Out.Step("Running");
        Out.Info($"Landing page:  {b}/");
        Out.Info($"Sign in:       {b}/account/login");
        Out.Info($"Health:        {b}/health   (live: {b}/health/live, ready: {b}/health/ready)");
        Out.Info($"Demo accounts: {string.Join(", ", DemoAccounts.Select(a => a.User))}");
        Out.Info("The local demo password is documented in the README (local runs only).");
        return 0;
    }

    public static async Task<int> DownAsync()
    {
        Out.Step("Stop the local stack (docker compose down; volumes and data are kept)");
        var r = await Proc.Compose(["down"]);
        Out.Info("Reminder: `docker compose down --volumes` deletes the database and stored files; this tool never runs it on your stack.");
        return r.Ok ? 0 : Fail("docker compose down failed.");
    }

    public static async Task<int> MigrateAsync()
    {
        Out.Step("Apply database migrations only (docker compose run --rm migrate migrate --no-seed)");
        var r = await Proc.Compose(["run", "--rm", "migrate", "migrate", "--no-seed"]);
        return r.Ok ? Done("Migrations applied.") : Fail("Migration failed. Is Docker running? Try: dotnet run --project tools/Dev -- up");
    }

    public static async Task<int> SeedAsync(Options o)
    {
        if (o.Flag("--reset"))
        {
            if (!o.Flag("--yes"))
            {
                Out.Fail("seed --reset is DESTRUCTIVE: it deletes all application data in the local database and reseeds it.");
                Out.Info("Re-run with --yes if you are sure: dotnet run --project tools/Dev -- seed --reset --yes");
                return 2;
            }
            Out.Step("DESTRUCTIVE: reset and reseed the local database (docker compose run --rm migrate reset --yes)");
            var reset = await Proc.Compose(["run", "--rm", "migrate", "reset", "--yes"]);
            return reset.Ok ? Done("Database reset and reseeded.") : Fail("Reset failed (see output above).");
        }
        if (o.Flag("--yes")) Out.Warn("--yes has no effect without --reset.");
        Out.Step("Seed (idempotent; docker compose run --rm migrate seed)");
        var r = await Proc.Compose(["run", "--rm", "migrate", "seed"]);
        return r.Ok ? Done("Seed finished.") : Fail("Seed failed. Is Docker running? Try: dotnet run --project tools/Dev -- up");
    }

    public static int Fail(string message)
    {
        Out.Fail(message);
        return 1;
    }

    private static int Done(string message)
    {
        Out.Ok(message);
        return 0;
    }
}
