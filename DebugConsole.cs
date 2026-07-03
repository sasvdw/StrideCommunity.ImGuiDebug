using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

using Stride.Core;
using Stride.Engine;
using Stride.Games;
using Stride.Input;

using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using static StrideCommunity.ImGuiDebug.ImGuiExtension;

namespace StrideCommunity.ImGuiDebug;

/// <summary>
/// A Quake/Source-style in-game developer console. Persistent (a plain <see cref="GameSystem"/>, not a
/// self-disposing <see cref="BaseWindow"/>) so it survives when every debug window is closed and can
/// reopen them. Owns a <see cref="CommandRegistry"/> and a <see cref="WindowRegistry"/>; registers
/// itself as a service so any system can call <see cref="Register"/> to add commands.
///
/// Toggle with <see cref="ToggleKey"/> (default <c>`</c>/tilde). Built-in commands: help, clear, echo,
/// windows, open, close, toggle.
/// </summary>
public class DebugConsole : GameSystem, IConsoleOutput
{
    const int MaxLog = 500;
    const int MaxHistory = 200;

    public Keys ToggleKey { get; set; } = Keys.OemTilde;

    readonly CommandRegistry _commands;
    readonly WindowRegistry _windows;
    readonly InputManager _inputManager;

    readonly List<(string text, LogLevel level)> _log = new();
    readonly List<string> _history = new();
    int _historyPos = -1;

    readonly string _historyDir;
    readonly string _historyPath;
    bool _historyWarned;

    readonly ImGuiInputTextCallback _inputCallback;
    ImGuiSystem _imgui;
    string _input = "";
    bool _visible = true;
    bool _focusInput = true; // grab the input box the first time the console appears
    bool _scrollToBottom;

    public unsafe DebugConsole(IServiceRegistry services) : base(services)
    {
        _inputManager = Services.GetService<InputManager>();

        _commands = new CommandRegistry();
        _windows = new WindowRegistry(Services);
        _windows.DiscoverWindows();
        RegisterBuiltins();

        _historyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrideImGuiConsole");
        _historyPath = Path.Combine(_historyDir, "history.txt");
        LoadHistory();

        _inputCallback = OnTextEdit;

        Enabled = true;
        UpdateOrder = 1000; // after ImGuiSystem's NewFrame; reconciled in Update if needed
        Services.AddService(this);
        Game.GameSystems.Add(this);

        Print("Debug console ready. Press ` (tilde) to toggle, 'help' for commands.");
    }

    // ---- public extension surface -------------------------------------------------------------

    /// <summary> Register a console command. </summary>
    public void Register(string name, ConsoleCommand handler, string help = "")
        => _commands.Register(name, handler, help);

    /// <summary> Register a debug window under a name for <c>open</c>/<c>close</c>/<c>toggle</c>. </summary>
    public void RegisterWindow(string name, Func<IServiceRegistry, BaseWindow> factory)
        => _windows.RegisterWindow(name, factory);

    /// <summary> Run a command line programmatically (autoexec, config files, key bindings). </summary>
    public void Execute(string line)
    {
        Print("] " + line, LogLevel.Echo);
        _commands.Execute(line, this);
    }

    /// <summary> Append a line to the console log. </summary>
    public void Print(string line, LogLevel level = LogLevel.Info)
    {
        _log.Add(($"[{DateTime.Now:HH:mm:ss}] {line}", level));
        if (_log.Count > MaxLog)
            _log.RemoveRange(0, _log.Count - MaxLog);
        _scrollToBottom = true;
    }

    // ---- game loop ----------------------------------------------------------------------------

    public override void Update(GameTime gameTime)
    {
        _imgui ??= Services.GetService<ImGuiSystem>();
        if (_imgui is null)
            return;

        // Must draw after ImGuiSystem.Update (NewFrame) and before its EndDraw (Render).
        if (UpdateOrder <= _imgui.UpdateOrder)
        {
            UpdateOrder = _imgui.UpdateOrder + 1;
            return;
        }

        _windows.Prune();

        if (_inputManager != null && _inputManager.IsKeyPressed(ToggleKey))
        {
            _visible = !_visible;
            if (_visible) _focusInput = true;
            else _input = "";
        }

        if (_visible)
            DrawConsole();
    }

    void DrawConsole()
    {
        SetNextWindowSize(new Vector2(680, 400), ImGuiCond.FirstUseEver);
        using (Window("Debug Console", ref _visible, out bool collapsed))
        {
            if (collapsed)
                return;

            float reserve = GetFrameHeightWithSpacing();
            using (Child(size: new Vector2(0, -reserve)))
            {
                foreach (var (text, level) in _log)
                {
                    PushStyleColor(ImGuiCol.Text, ColorFor(level));
                    TextUnformatted(text);
                    PopStyleColor();
                }
                if (_scrollToBottom)
                {
                    SetScrollHereY(1f);
                    _scrollToBottom = false;
                }
            }

            Separator();
            DrawInput();
        }
    }

    void DrawInput()
    {
        _input = StripToggleChars(_input ?? "");

        if (_focusInput)
        {
            SetKeyboardFocusHere();
            _focusInput = false;
        }

        const ImGuiInputTextFlags flags = ImGuiInputTextFlags.EnterReturnsTrue
            | ImGuiInputTextFlags.CallbackHistory
            | ImGuiInputTextFlags.CallbackCompletion;

        PushItemWidth(GetContentRegionAvail().X);
        bool submit = InputText("##cmdline", ref _input, (nuint)256, flags, _inputCallback);
        PopItemWidth();

        if (submit)
        {
            string line = (_input ?? "").Trim();
            _input = "";
            if (line.Length > 0)
                Submit(line);
            _focusInput = true; // keep focus for the next command
        }
    }

    void Submit(string line)
    {
        Print("] " + line, LogLevel.Echo);
        AddHistory(line);
        _commands.Execute(line, this);
    }

    // ---- InputText callback (history + completion) ---------------------------------------------

    unsafe int OnTextEdit(ImGuiInputTextCallbackData* data)
    {
        if (data->EventFlag == ImGuiInputTextFlags.CallbackHistory)
            OnHistory(data);
        else if (data->EventFlag == ImGuiInputTextFlags.CallbackCompletion)
            OnCompletion(data);
        return 0;
    }

    unsafe void OnHistory(ImGuiInputTextCallbackData* data)
    {
        if (_history.Count == 0)
            return;

        int prev = _historyPos;
        if (data->EventKey == ImGuiKey.UpArrow)
        {
            if (_historyPos < 0) _historyPos = _history.Count - 1;
            else if (_historyPos > 0) _historyPos--;
        }
        else if (data->EventKey == ImGuiKey.DownArrow)
        {
            if (_historyPos >= 0 && ++_historyPos >= _history.Count)
                _historyPos = -1;
        }

        if (prev != _historyPos)
            ReplaceBuffer(data, _historyPos >= 0 ? _history[_historyPos] : "");
    }

    unsafe void OnCompletion(ImGuiInputTextCallbackData* data)
    {
        string text = Utf8ToString(data->Buf, data->BufTextLen);
        string[] parts = text.Split(' ');
        int last = parts.Length - 1;
        string prefix = parts[last];

        IReadOnlyList<string> candidates;
        if (last == 0)
        {
            candidates = _commands.Complete(prefix);
        }
        else if (IsWindowCommand(parts[0]))
        {
            var list = new List<string>();
            foreach (var n in _windows.Names)
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(n);
            candidates = list;
        }
        else
        {
            return;
        }

        if (candidates.Count == 0)
            return;

        string completion = candidates.Count == 1 ? candidates[0] : LongestCommonPrefix(candidates);
        if (candidates.Count > 1)
            Print("candidates: " + string.Join("  ", candidates));
        if (completion.Length < prefix.Length)
            return;

        parts[last] = completion;
        string newText = string.Join(' ', parts);
        if (candidates.Count == 1)
            newText += " ";
        ReplaceBuffer(data, newText);
    }

    static unsafe void ReplaceBuffer(ImGuiInputTextCallbackData* data, string text)
    {
        data->DeleteChars(0, data->BufTextLen);
        if (!string.IsNullOrEmpty(text))
            data->InsertChars(data->CursorPos, text);
    }

    static unsafe string Utf8ToString(byte* buf, int len)
        => (buf == null || len <= 0) ? "" : Encoding.UTF8.GetString(buf, len);

    // ---- built-in commands ---------------------------------------------------------------------

    void RegisterBuiltins()
    {
        _commands.Register("help", CmdHelp, "help [command] - list commands, or show help for one");
        _commands.Register("clear", (a, o) => _log.Clear(), "clear - clear the log");
        _commands.Register("echo", (a, o) => o.Print(string.Join(' ', a)), "echo <text> - print text");
        _commands.Register("windows", CmdWindows, "windows - list debug windows and open state");
        _commands.Register("open", CmdOpen, "open <window> - open a debug window");
        _commands.Register("close", CmdClose, "close <window> - close a debug window");
        _commands.Register("toggle", CmdToggle, "toggle <window> - toggle a debug window");
    }

    void CmdHelp(string[] args, IConsoleOutput o)
    {
        if (args.Length > 0)
        {
            if (_commands.TryGetHelp(args[0], out var help))
                o.Print(string.IsNullOrEmpty(help) ? args[0] : help);
            else
                o.Print($"No such command: '{args[0]}'", LogLevel.Error);
            return;
        }
        o.Print("Commands:");
        foreach (var (name, help) in _commands.Commands)
            o.Print($"  {name,-10} {help}");
    }

    void CmdWindows(string[] args, IConsoleOutput o)
    {
        o.Print("Windows:");
        foreach (var (name, open) in _windows.Windows)
            o.Print($"  {name,-18} {(open ? "[open]" : "[closed]")}");
    }

    void CmdOpen(string[] args, IConsoleOutput o)
    {
        if (args.Length == 0) { o.Print("usage: open <window>", LogLevel.Warning); return; }
        o.Print(_windows.Open(args[0], out var err) ? $"Opened {args[0]}." : err,
            err is null ? LogLevel.Info : LogLevel.Error);
    }

    void CmdClose(string[] args, IConsoleOutput o)
    {
        if (args.Length == 0) { o.Print("usage: close <window>", LogLevel.Warning); return; }
        o.Print(_windows.Close(args[0]) ? $"Closed {args[0]}." : $"'{args[0]}' is not open.",
            LogLevel.Info);
    }

    void CmdToggle(string[] args, IConsoleOutput o)
    {
        if (args.Length == 0) { o.Print("usage: toggle <window>", LogLevel.Warning); return; }
        if (!_windows.IsRegistered(args[0])) { o.Print($"No window named '{args[0]}'.", LogLevel.Error); return; }
        bool open = _windows.Toggle(args[0]);
        o.Print($"{args[0]} {(open ? "opened" : "closed")}.");
    }

    // ---- history persistence -------------------------------------------------------------------

    void AddHistory(string line)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
        {
            _history.Add(line);
            AppendHistoryFile(line);
            if (_history.Count > MaxHistory)
                _history.RemoveRange(0, _history.Count - MaxHistory);
        }
        _historyPos = -1;
    }

    void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath))
                return;
            var lines = File.ReadAllLines(_historyPath);
            int start = Math.Max(0, lines.Length - MaxHistory);
            for (int i = start; i < lines.Length; i++)
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    _history.Add(lines[i]);
        }
        catch (Exception e)
        {
            Print("Could not read console history: " + e.Message, LogLevel.Warning);
        }
    }

    void AppendHistoryFile(string line)
    {
        try
        {
            Directory.CreateDirectory(_historyDir);
            File.AppendAllText(_historyPath, line + Environment.NewLine);
        }
        catch (Exception e)
        {
            if (!_historyWarned)
            {
                _historyWarned = true;
                Print("Console history not saved: " + e.Message, LogLevel.Warning);
            }
        }
    }

    protected override void Destroy()
    {
        try
        {
            if (_history.Count > 0)
            {
                Directory.CreateDirectory(_historyDir);
                File.WriteAllLines(_historyPath, _history); // trim file to the capped in-memory history
            }
        }
        catch { /* history is best-effort */ }
        base.Destroy();
    }

    // ---- helpers -------------------------------------------------------------------------------

    static bool IsWindowCommand(string cmd)
        => string.Equals(cmd, "open", StringComparison.OrdinalIgnoreCase)
        || string.Equals(cmd, "close", StringComparison.OrdinalIgnoreCase)
        || string.Equals(cmd, "toggle", StringComparison.OrdinalIgnoreCase);

    static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return "";
        string p = items[0];
        for (int i = 1; i < items.Count; i++)
        {
            int j = 0;
            while (j < p.Length && j < items[i].Length
                   && char.ToLowerInvariant(p[j]) == char.ToLowerInvariant(items[i][j]))
                j++;
            p = p.Substring(0, j);
            if (p.Length == 0) break;
        }
        return p;
    }

    static string StripToggleChars(string s)
    {
        if (string.IsNullOrEmpty(s) || (s.IndexOf('`') < 0 && s.IndexOf('~') < 0))
            return s;
        return s.Replace("`", "").Replace("~", "");
    }

    static Vector4 ColorFor(LogLevel level) => level switch
    {
        LogLevel.Warning => new Vector4(1f, 0.8f, 0.3f, 1f),
        LogLevel.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
        LogLevel.Echo => new Vector4(0.5f, 0.8f, 1f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };
}
