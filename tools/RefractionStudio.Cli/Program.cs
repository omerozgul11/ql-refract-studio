using System.Globalization;
using RefractionStudio.Cli;

// Refraction Studio command-line tool: batch analysis, reports, sample project, validation, signing keys.
var commands = new Dictionary<string, (string Help, Func<Args, Task<int>> Run)>(StringComparer.OrdinalIgnoreCase)
{
    ["sample"] = ("sample --out <file.rfsx> [--work <dir>]                       Build the distributed sample project", Commands.SampleAsync),
    ["analyze"] = ("analyze <project.rfsx>                                          Print the analysis summary", Commands.AnalyzeAsync),
    ["report"] = ("report <project.rfsx> --out <report.pdf> [--units si|us] [--paper a4|letter]  Generate the PDF report", Commands.ReportAsync),
    ["export"] = ("export <project.rfsx> --out <folder> [--units si|us]           Export XLSX, CSV, audit JSON and figures", Commands.ExportAsync),
    ["validate"] = ("validate --out <folder>                                         Run the scientific validation suite and write the report", Commands.ValidateAsync),
    ["keys"] = ("keys --out <folder>                                             Generate an ES256 signing key pair (licences or releases)", Commands.KeysAsync),
    ["sign-manifest"] = ("sign-manifest <manifest.json> --key <private.pem> --out <signed.json>   Sign an update manifest", Commands.SignManifestAsync),
};

if (args.Length == 0 || args[0] is "-h" or "--help" || !commands.ContainsKey(args[0]))
{
    Console.WriteLine("Refraction Studio CLI");
    Console.WriteLine();
    foreach (var (name, (help, _)) in commands) Console.WriteLine("  " + help);
    return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 2;
}

try
{
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
    return await commands[args[0]].Run(new Args(args.Skip(1).ToArray()));
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
    or RefractionStudio.Persistence.ProjectFileException or RefractionStudio.Formats.SeismicFormatException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
