namespace TermSquared.Protocols.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TERMSQUARED_RUN_INTEGRATION"), "1", StringComparison.Ordinal))
            Skip = "Set TERMSQUARED_RUN_INTEGRATION=1 to run real network integration tests.";
    }
}
