using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Matchmate.ServiceDefaults;

public static class RuntimeMode
{
    public static bool Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var hosted = configuration.GetValue<bool>("Runtime:Hosted");
        if (!hosted && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Runtime:Hosted=true is required outside Development/Testing; local replay mode is not a production fallback.");
        return hosted;
    }

    public static bool RecordedImportEnabled(IConfiguration configuration, bool hosted)
    {
        var enabled = configuration.GetValue<bool?>("RecordedImport:Enabled");
        if (hosted && enabled != false)
            throw new InvalidOperationException("Hosted runtime requires RecordedImport:Enabled=false until the Azure pipeline is integrated.");
        return enabled ?? true;
    }
}
