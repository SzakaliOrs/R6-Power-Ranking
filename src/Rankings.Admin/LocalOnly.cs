using System.Net;

namespace Rankings.Admin;

/// <summary>The checks that keep the admin dashboard to the admin's own machine.</summary>
public static class LocalOnly
{
    /// <summary>Header every change request must carry; a browser only sends it from the dashboard's own page.</summary>
    public const string ChangeHeader = "X-Rankings-Admin";

    public static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>GitHub Actions and most CI systems set CI; GitHub also sets GITHUB_ACTIONS.</summary>
    public static bool RunningOnCi() =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a request is refused, or null to let it through.</summary>
    public static string? Refusal(IPAddress? remoteAddress, string method, string? changeHeader)
    {
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
            return "The admin dashboard only answers requests from this machine.";
        var isChange = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
        if (isChange && changeHeader != "1")
            return "Changes are only accepted from the dashboard's own page.";
        return null;
    }
}
