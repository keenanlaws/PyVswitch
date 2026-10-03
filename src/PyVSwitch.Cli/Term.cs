using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using PyVSwitch.Models;

namespace PyVSwitch.Cli;

internal static partial class Term
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public static bool Color { get; private set; }
    public static bool Interactive { get; private set; }

    public static void Initialize()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console attached (for example under a service); output still works.
        }

        Interactive = !Console.IsOutputRedirected;
        Color = Interactive && Environment.GetEnvironmentVariable("NO_COLOR") is null && EnableAnsi();
    }

    public static string Bold(string text) => Wrap(text, "1");
    public static string Dim(string text) => Wrap(text, "2");
    public static string Red(string text) => Wrap(text, "91");
    public static string Green(string text) => Wrap(text, "92");
    public static string Yellow(string text) => Wrap(text, "93");
    public static string Blue(string text) => Wrap(text, "94");
    public static string Cyan(string text) => Wrap(text, "96");

    private static string Wrap(string text, string code) => Color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    /// <summary>Prints rows as aligned columns. The styler receives the column index and the padded cell.</summary>
    public static void Table(string[] headers, IReadOnlyList<string[]> rows, Func<int, int, string, string>? style = null)
    {
        var widths = headers.Select((header, column) => Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(row => row[column].Length))).ToArray();
        Console.WriteLine(Dim(string.Join("  ", headers.Select((header, column) => header.PadRight(widths[column]))).TrimEnd()));
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Select((cell, column) =>
            {
                var padded = column == headers.Length - 1 ? cell : cell.PadRight(widths[column]);
                return style is null ? padded : style(r, column, padded);
            });
            Console.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }

    public static void Json(JsonNode? data)
    {
        Console.Out.WriteLine(new JsonObject { ["ok"] = true, ["data"] = data }.ToJsonString(JsonShapes.Indented));
    }

    public static void Error(bool json, string code, string message)
    {
        if (json)
        {
            var error = new JsonObject { ["code"] = code, ["message"] = message };
            Console.Out.WriteLine(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(JsonShapes.Indented));
        }
        else
        {
            Console.Error.WriteLine($"{Red("error")}: {message}");
        }
    }

    public static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>True when this process was started from Explorer or a shortcut and owns its console window.</summary>
    public static bool OwnsConsole()
    {
        try
        {
            var buffer = new uint[2];
            return GetConsoleProcessList(buffer, 2) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnableAnsi()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            return GetConsoleMode(handle, out var mode) &&
                   ((mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing));
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr handle, uint mode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}

/// <summary>Writes installer progress synchronously so lines never arrive out of order.</summary>
internal sealed class ConsoleProgress : IProgress<InstallProgress>
{
    private string _lastStage = "";
    private bool _barOpen;

    public void Report(InstallProgress value)
    {
        if (value.Stage == "download" && Term.Interactive && value.TotalBytes > 0)
        {
            const int width = 28;
            var filled = (int)Math.Round(value.Fraction * width);
            var bar = new string('█', filled) + new string('░', width - filled);
            Console.Write($"\r  {Term.Blue(bar)} {value.Fraction * 100,3:0}%  {Term.Megabytes(value.BytesReceived)} / {Term.Megabytes(value.TotalBytes)}   ");
            _barOpen = true;
            _lastStage = value.Stage;
            return;
        }

        if (value.Stage == _lastStage && value.Stage == "download")
        {
            return;
        }

        CloseBar();
        _lastStage = value.Stage;
        if (value.Stage != "done")
        {
            Console.WriteLine($"  {Term.Dim("›")} {value.Message}");
        }
    }

    public void CloseBar()
    {
        if (_barOpen)
        {
            Console.WriteLine();
            _barOpen = false;
        }
    }
}
