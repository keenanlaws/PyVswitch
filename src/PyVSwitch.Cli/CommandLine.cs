namespace PyVSwitch.Cli;

internal sealed class CommandLine
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--python", "--scope", "--arch", "--series", "--requirement", "--output", "--target-dir", "--only"
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-p"] = "--python",
        ["-r"] = "--requirement",
        ["-o"] = "--output",
        ["-y"] = "--yes",
        ["-h"] = "--help",
        ["-V"] = "--version",
        ["-v"] = "--version",
        ["-U"] = "--upgrade",
        ["-a"] = "--all"
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private set; } = "";
    public List<string> Positionals { get; } = [];
    /// <summary>Arguments forwarded untouched to python or pip.</summary>
    public List<string> Rest { get; } = [];

    public bool Flag(string name) => _options.ContainsKey(name);

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                line.Rest.AddRange(args[(i + 1)..]);
                break;
            }

            // `run` and `pip` hand everything after their own options to the child process.
            var passthrough = line.Command is "run" or "pip";

            if (arg.Length > 1 && arg[0] == '-')
            {
                var name = arg;
                string? inlineValue = null;
                var equals = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
                {
                    name = arg[..equals];
                    inlineValue = arg[(equals + 1)..];
                }

                name = Aliases.GetValueOrDefault(name, name);
                if (passthrough && name is not ("--python" or "--json"))
                {
                    line.Rest.AddRange(args[i..]);
                    break;
                }

                if (ValueOptions.Contains(name))
                {
                    if (inlineValue is null)
                    {
                        if (i + 1 >= args.Length)
                        {
                            throw new PyvsException("usage", $"Option {arg} needs a value.");
                        }

                        inlineValue = args[++i];
                    }

                    line._options[name] = inlineValue;
                }
                else
                {
                    line._options[name] = inlineValue ?? "true";
                }
            }
            else if (line.Command.Length == 0)
            {
                line.Command = arg.ToLowerInvariant();
            }
            else if (passthrough)
            {
                line.Rest.AddRange(args[i..]);
                break;
            }
            else
            {
                line.Positionals.Add(arg);
            }
        }

        return line;
    }
}
