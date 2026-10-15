using EventPermits.Web.Hosting;

if (args.Length > 0 && AdminCli.Commands.Contains(args[0]))
{
    return await AdminCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddEventPermits(HostRole.Web);
var app = builder.Build();
app.UseEventPermits();
app.Run();
return 0;

public partial class Program;
