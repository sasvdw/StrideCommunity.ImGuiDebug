using System;
using System.Collections.Generic;
using System.Text;

namespace StrideCommunity.ImGuiDebug;

/// <summary> Severity / kind of a console log line, used for colouring. </summary>
public enum LogLevel { Info, Warning, Error, Echo }

/// <summary> Sink a <see cref="ConsoleCommand"/> writes its output to. </summary>
public interface IConsoleOutput
{
    void Print(string line, LogLevel level = LogLevel.Info);
}

/// <summary> A console command: receives its arguments (command name stripped) and an output sink. </summary>
public delegate void ConsoleCommand(string[] args, IConsoleOutput output);

/// <summary>
/// A name → handler map plus a small command-line tokenizer and dispatcher.
/// Deliberately free of any ImGui/Stride dependency so it can be unit tested in isolation.
/// </summary>
public class CommandRegistry
{
    readonly Dictionary<string, (ConsoleCommand handler, string help)> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, ConsoleCommand handler, string help = "")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Command name must be non-empty", nameof(name));
        _commands[name] = (handler ?? throw new ArgumentNullException(nameof(handler)), help ?? "");
    }

    public bool Unregister(string name) => _commands.Remove(name);

    public bool Contains(string name) => _commands.ContainsKey(name);

    public bool TryGetHelp(string name, out string help)
    {
        if (_commands.TryGetValue(name, out var entry)) { help = entry.help; return true; }
        help = null;
        return false;
    }

    /// <summary> All registered commands, sorted by name. </summary>
    public IReadOnlyList<(string name, string help)> Commands
    {
        get
        {
            var list = new List<(string, string)>(_commands.Count);
            foreach (var kv in _commands)
                list.Add((kv.Key, kv.Value.help));
            list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }

    /// <summary> Tokenize <paramref name="line"/>, dispatch to the named command, report errors to <paramref name="output"/>. </summary>
    public void Execute(string line, IConsoleOutput output)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));

        var tokens = Tokenize(line);
        if (tokens.Length == 0)
            return;

        var name = tokens[0];
        if (_commands.TryGetValue(name, out var entry) == false)
        {
            output.Print($"Unknown command: '{name}'. Type 'help' for a list.", LogLevel.Error);
            return;
        }

        var args = new string[tokens.Length - 1];
        Array.Copy(tokens, 1, args, 0, args.Length);
        try
        {
            entry.handler(args, output);
        }
        catch (Exception e)
        {
            output.Print($"Command '{name}' threw: {e.Message}", LogLevel.Error);
        }
    }

    /// <summary> Command names starting with <paramref name="prefix"/> (case-insensitive), sorted. </summary>
    public IReadOnlyList<string> Complete(string prefix)
    {
        prefix ??= "";
        var matches = new List<string>();
        foreach (var name in _commands.Keys)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                matches.Add(name);
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }

    /// <summary>
    /// Split a command line into tokens on whitespace, honouring "double quoted" segments so an
    /// argument may contain spaces, and treating <c>\</c> as an escape for the following character.
    /// </summary>
    public static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(line))
            return tokens.ToArray();

        var sb = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(line[++i]);
                hasToken = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true; // an explicit "" is a real (empty) token
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    hasToken = false;
                }
            }
            else
            {
                sb.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            tokens.Add(sb.ToString());
        return tokens.ToArray();
    }
}
