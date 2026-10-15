namespace EventPermits.Infrastructure.Seeding;

public sealed partial class Seeder
{
    /// <summary>Case data (E12-01) is added once the workflow services exist.</summary>
    private static Task<(int Applications, int Files)> SeedCasesAsync(CancellationToken ct) => Task.FromResult((0, 0));
}
