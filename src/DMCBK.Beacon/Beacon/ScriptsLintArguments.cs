namespace DMCBK.Core.Beacon;

internal static class ScriptsLintArguments
{
    public static bool TryParse(string[] args, out BeaconLintRequest? parsed, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        parsed = null;
        error = string.Empty;

        string format = "text";
        int? targetLib = null;
        bool strict = false;
        bool fix = false;
        const bool useStdin = false;
        const string? stdinName = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string[] parts = arg.StartsWith("--", StringComparison.Ordinal) ? arg.Split('=', 2) : [arg];
            string flag = parts[0]; string? attached = parts.Length == 2 ? parts[1] : null;

            switch (flag)
            {
                case "--format":
                    string? value = attached ?? (TryTakeNextValue(args, ref i, out string? next) ? next : null);
                    if (value is not ("text" or "json"))
                    {
                        error = $"lint: --format must be 'text' or 'json', not '{value ?? string.Empty}'. "
                            + "Usage: lint <file...> [--format text|json] [--target-lib N] [--strict] [--fix] [--stdin] [--stdin-name <name>]";
                        return false;
                    }

                    format = value;
                    break;
                case "--target-lib":
                    string? raw = attached ?? (TryTakeNextValue(args, ref i, out string? libNext) ? libNext : null);
                    if (!int.TryParse(raw, out int lib) || lib < 1)
                    {
                        error = $"lint: --target-lib needs a positive integer, not '{raw ?? string.Empty}'.";
                        return false;
                    }

                    targetLib = lib;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--fix":
                    fix = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"lint: unknown flag '{flag}'. "
                            + "Usage: lint <file...> [--format text|json] [--target-lib N] [--strict] [--fix] [--stdin] [--stdin-name <name>]";
                        return false;
                    }

                    files.Add(arg);
                    break;
            }
        }

        parsed = new BeaconLintRequest(files, format, targetLib, strict, fix, useStdin, stdinName);
        return true;
    }
    private static bool TryTakeNextValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 < args.Length) { value = args[++index]; return true; }
        value = null; return false;
    }
}
