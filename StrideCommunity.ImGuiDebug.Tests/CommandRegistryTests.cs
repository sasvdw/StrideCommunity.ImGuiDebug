using System.Collections.Generic;
using StrideCommunity.ImGuiDebug;
using Xunit;

namespace StrideCommunity.ImGuiDebug.Tests;

public class CommandRegistryTests
{
    sealed class CapturingOutput : IConsoleOutput
    {
        public readonly List<(string line, LogLevel level)> Lines = new();
        public void Print(string line, LogLevel level = LogLevel.Info) => Lines.Add((line, level));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        Assert.Equal(new[] { "open", "PerfMonitor" }, CommandRegistry.Tokenize("open   PerfMonitor"));
    }

    [Fact]
    public void Tokenize_EmptyOrWhitespace_YieldsNoTokens()
    {
        Assert.Empty(CommandRegistry.Tokenize(""));
        Assert.Empty(CommandRegistry.Tokenize("   "));
        Assert.Empty(CommandRegistry.Tokenize(null));
    }

    [Fact]
    public void Tokenize_HonorsQuotedSegments()
    {
        Assert.Equal(new[] { "echo", "hello world", "next" }, CommandRegistry.Tokenize("echo \"hello world\" next"));
    }

    [Fact]
    public void Tokenize_EscapeAndEmptyQuotes()
    {
        Assert.Equal(new[] { "a b" }, CommandRegistry.Tokenize("a\\ b"));
        Assert.Equal(new[] { "" }, CommandRegistry.Tokenize("\"\""));
    }

    [Fact]
    public void Execute_DispatchesWithArgsStripped()
    {
        var reg = new CommandRegistry();
        string[] received = null;
        reg.Register("greet", (args, o) => received = args);

        reg.Execute("greet alice bob", new CapturingOutput());

        Assert.Equal(new[] { "alice", "bob" }, received);
    }

    [Fact]
    public void Execute_UnknownCommand_PrintsError()
    {
        var reg = new CommandRegistry();
        var output = new CapturingOutput();

        reg.Execute("nope", output);

        Assert.Single(output.Lines);
        Assert.Equal(LogLevel.Error, output.Lines[0].level);
        Assert.Contains("Unknown command", output.Lines[0].line);
    }

    [Fact]
    public void Execute_HandlerThatThrows_IsReportedNotPropagated()
    {
        var reg = new CommandRegistry();
        reg.Register("boom", (a, o) => throw new System.InvalidOperationException("kaboom"));
        var output = new CapturingOutput();

        reg.Execute("boom", output); // must not throw

        Assert.Contains(output.Lines, l => l.level == LogLevel.Error && l.line.Contains("kaboom"));
    }

    [Fact]
    public void Execute_IsCaseInsensitive()
    {
        var reg = new CommandRegistry();
        bool ran = false;
        reg.Register("Open", (a, o) => ran = true);

        reg.Execute("oPeN x", new CapturingOutput());

        Assert.True(ran);
    }

    [Fact]
    public void Complete_ReturnsSortedPrefixMatches()
    {
        var reg = new CommandRegistry();
        reg.Register("close", (a, o) => { });
        reg.Register("clear", (a, o) => { });
        reg.Register("open", (a, o) => { });

        Assert.Equal(new[] { "clear", "close" }, reg.Complete("cl"));
        Assert.Equal(new[] { "clear", "close", "open" }, reg.Complete(""));
        Assert.Empty(reg.Complete("zzz"));
    }
}
