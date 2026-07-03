using System;
using System.Collections.Generic;
using System.Reflection;
using Stride.Core;

namespace StrideCommunity.ImGuiDebug;

/// <summary>
/// Tracks the set of debug windows that can be opened by name and the currently-live instances.
/// Windows self-dispose when closed (see <see cref="BaseWindow"/>), so re-opening means re-running
/// a factory; <see cref="Prune"/> reconciles instances the user closed via the window's [x].
/// No ImGui dependency — the console owns one of these and drives it from its command handlers.
/// </summary>
public class WindowRegistry
{
    readonly IServiceRegistry _services;
    readonly Dictionary<string, Func<IServiceRegistry, BaseWindow>> _factories =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BaseWindow> _instances =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowRegistry(IServiceRegistry services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary> Register a window under <paramref name="name"/>, e.g. for windows needing extra ctor args. </summary>
    public void RegisterWindow(string name, Func<IServiceRegistry, BaseWindow> factory)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Window name must be non-empty", nameof(name));
        _factories[name] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Reflect over loaded assemblies and register every concrete <see cref="BaseWindow"/> subclass
    /// exposing a public <c>(IServiceRegistry)</c> constructor, keyed by its type name. Existing
    /// manual registrations are preserved.
    /// </summary>
    public void DiscoverWindows()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch (Exception) { continue; }

            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || !typeof(BaseWindow).IsAssignableFrom(t))
                    continue;
                if (_factories.ContainsKey(t.Name))
                    continue;
                var ctor = t.GetConstructor(new[] { typeof(IServiceRegistry) });
                if (ctor is null)
                    continue;
                _factories[t.Name] = s => (BaseWindow)ctor.Invoke(new object[] { s });
            }
        }
    }

    public bool IsRegistered(string name) => _factories.ContainsKey(name);

    public bool IsOpen(string name)
    {
        Prune();
        return _instances.ContainsKey(name);
    }

    /// <summary> Open the named window. False (with <paramref name="error"/>) if unknown or already open. </summary>
    public bool Open(string name, out string error)
    {
        Prune();
        error = null;

        if (_instances.ContainsKey(name))
        {
            error = $"'{name}' is already open.";
            return false;
        }
        if (_factories.TryGetValue(name, out var factory) == false)
        {
            error = $"No window named '{name}'. Type 'windows' to list them.";
            return false;
        }

        var window = factory(_services);
        if (window is null)
        {
            error = $"Factory for '{name}' returned null.";
            return false;
        }

        _instances[name] = window;
        return true;
    }

    /// <summary> Close the named window if open. Returns false if it wasn't open. </summary>
    public bool Close(string name)
    {
        Prune();
        if (_instances.TryGetValue(name, out var window) == false)
            return false;
        _instances.Remove(name);
        window.Close();
        return true;
    }

    /// <summary> Toggle the named window; returns its resulting open state. </summary>
    public bool Toggle(string name)
    {
        if (IsOpen(name))
        {
            Close(name);
            return false;
        }
        Open(name, out _);
        return IsOpen(name);
    }

    /// <summary> Drop instances that were closed via the window's [x] so state stays truthful. </summary>
    public void Prune()
    {
        List<string> dead = null;
        foreach (var kv in _instances)
        {
            if (kv.Value.IsOpen == false)
                (dead ??= new List<string>()).Add(kv.Key);
        }
        if (dead != null)
            foreach (var name in dead)
                _instances.Remove(name);
    }

    /// <summary> Registered window names, sorted (for autocomplete). </summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            var names = new List<string>(_factories.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    /// <summary> Each registered window and whether it is currently open, sorted by name. </summary>
    public IReadOnlyList<(string name, bool open)> Windows
    {
        get
        {
            Prune();
            var list = new List<(string, bool)>(_factories.Count);
            foreach (var name in _factories.Keys)
                list.Add((name, _instances.ContainsKey(name)));
            list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
