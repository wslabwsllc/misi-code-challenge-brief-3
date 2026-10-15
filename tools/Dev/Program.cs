using Dev;

// Cross-platform developer command runner (story E0-03 AC5). Run from anywhere inside the repository:
//   dotnet run --project tools/Dev -- <command> [options]
// Child processes are started with argument lists, never through a shell, so zsh, bash and PowerShell behave the same.
var command = args.FirstOrDefault() ?? "help";
try
{
    var options = Options.Parse(args.Skip(1).ToArray());
    return command switch
    {
        "doctor" => await Commands.DoctorAsync(),
        "setup" => await Commands.SetupAsync(),
        "build" => await Commands.BuildAsync(),
        "up" => await Commands.UpAsync(),
        "down" => await Commands.DownAsync(),
        "migrate" => await Commands.MigrateAsync(),
        "seed" => await Commands.SeedAsync(options),
        "test" => await TestCommand.RunAsync(options),
        "smoke" => await Smoke.RunAsync(options),
        "scan" => await Scan.RunAsync(),
        "screenshots" => await TestCommand.ScreenshotsAsync(options),
        "help" or "--help" or "-h" => Commands.Help(0),
        _ => Commands.Help(2, $"Unknown command '{command}'."),
    };
}
catch (DevException ex)
{
    Out.Fail(ex.Message);
    return 1;
}
