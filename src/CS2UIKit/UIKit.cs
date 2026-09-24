using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace CS2UIKit;

/// <summary>
/// Entry point. Call <see cref="Init"/> once in your plugin's <c>Load</c> and <see cref="Shutdown"/> in <c>Unload</c>;
/// everything else — panels, menus, toasts, votes — is created after that.
///
/// <code>
/// public override void Load(bool hotReload)   => UIKit.Init(this, hotReload);
/// public override void Unload(bool hotReload) => UIKit.Shutdown();
/// </code>
///
/// What it does for you:
/// <list type="bullet">
/// <item>creates each panel's entity at the first safe moment of a map and recreates it after a map change;</item>
/// <item>routes every click to the panel that was clicked, so several panels can be live at once;</item>
/// <item>clears a slot's state when a new player takes it, so nobody inherits a captured cursor;</item>
/// <item>removes its entities on unload and after a crash, and only its own — other plugins' panels are left alone;</item>
/// <item><c>css_uikit</c> in the server console lists panels, entities and who has what open.</item>
/// </list>
/// </summary>
public static class UIKit
{
    private static BasePlugin? _plugin;
    private static Action<string>? _log;
    private static readonly List<Panel> Panels = new();

    /// <summary>
    /// A map is loaded and entities may be touched. On a cold start that is the first round start; touching the
    /// entity list earlier throws "Entity system yet is not initialized", and CounterStrikeSharp caches that failure
    /// for the life of the process — the plugin stays blind until the server restarts.
    /// </summary>
    public static bool WorldReady { get; private set; }

    public static bool Initialized => _plugin is not null;

    internal static BasePlugin Plugin => _plugin ?? throw new InvalidOperationException("Call UIKit.Init(this, hotReload) in Load first.");

    /// <param name="hotReload">The flag your <c>Load</c> received. It decides when entities may be created.</param>
    /// <param name="log">Optional sink for diagnostics, e.g. <c>m => Logger.LogInformation(m)</c>.</param>
    public static void Init(BasePlugin plugin, bool hotReload, Action<string>? log = null)
    {
        if (_plugin is not null) return;
        _plugin = plugin;
        _log = log;

        plugin.RegisterListener<Listeners.OnCustomHudClicked>(OnClicked);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnClientPutInServer>(OnPutInServer);
        plugin.RegisterListener<Listeners.OnClientDisconnect>(OnDisconnect);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.AddCommand("css_uikit", "CS2UIKit: panels, entities and open state", OnDiagnostics);

        if (!hotReload) return;
        WorldReady = true;
        SpawnAll();
    }

    /// <summary>Remove every entity this plugin created and release every cursor. Call it in <c>Unload</c>.</summary>
    public static void Shutdown()
    {
        if (_plugin is null) return;
        Toasts.Stop();
        Votes.Stop();
        foreach (var panel in Panels.ToList())
        {
            panel.HideAll();
            var entity = panel.Entity;
            if (entity is not null && entity.IsValid) entity.Remove();
            panel.Detach();
        }
        Panels.Clear();

        _plugin.RemoveListener<Listeners.OnCustomHudClicked>(OnClicked);
        _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        _plugin.RemoveListener<Listeners.OnClientPutInServer>(OnPutInServer);
        _plugin.RemoveListener<Listeners.OnClientDisconnect>(OnDisconnect);
        _plugin.DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RemoveCommand("css_uikit", OnDiagnostics);
        _plugin = null;
        WorldReady = false;
    }

    /// <summary>
    /// Recreate every panel entity. Since CS2 1.41.8.2 the game clears the "is set" flag of a slot's texts when a
    /// player takes the slot, and CounterStrikeSharp 1.0.374 only updates the value afterwards — the text stays
    /// empty until the map changes (CounterStrikeSharp PR #1434). A fresh entity has no entries, so every text is
    /// added anew with the flag up. Call it a moment after a player fully connects; toasts and a running vote card
    /// are cleared. Remove once CounterStrikeSharp ships the fix.
    /// </summary>
    public static void Rebuild()
    {
        if (_plugin is null || !WorldReady) return;
        Toasts.Reset();
        Votes.Reset();
        foreach (var panel in Panels)
        {
            panel.HideAll();
            var entity = panel.Entity;
            if (entity is not null && entity.IsValid) entity.Remove();
            panel.Detach();
        }
        SpawnAll();
        Log("panels rebuilt");
    }

    internal static void Log(string message) => _log?.Invoke("CS2UIKit: " + message);

    internal static void Register(Panel panel)
    {
        if (_plugin is null) throw new InvalidOperationException("Call UIKit.Init(this, hotReload) in Load before creating panels.");
        Panels.Add(panel);
        if (WorldReady) Spawn(panel);
    }

    /// <summary>
    /// A live player proves a map is loaded — a controller cannot exist before one. Lets a panel shown before the
    /// first round start (a hot reload without a round, a late plugin load) still get its entity.
    /// </summary>
    internal static void EnsureWorld(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        if (!WorldReady)
        {
            WorldReady = true;
            SpawnAll();
        }
    }

    // ── entities ─────────────────────────────────────────────────────────────────────────────────

    private static void SpawnAll()
    {
        // Leftovers from a previous load or a crash carry our layouts; remove them before making new ones.
        // Only entities with one of OUR layouts: another plugin's panels are not ours to delete.
        var ours = Panels.Select(p => p.Layout).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = 0;
        foreach (var old in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
        {
            if (!old.IsValid || !ours.Contains(old.StrLayout ?? string.Empty)) continue;
            if (Panels.Any(p => p.Owns(old))) continue;
            old.Remove();
            orphans++;
        }
        if (orphans > 0) Log($"removed {orphans} orphaned panel(s)");

        foreach (var panel in Panels) Spawn(panel);
    }

    private static void Spawn(Panel panel)
    {
        if (!WorldReady || panel.HasEntity) return;
        try
        {
            var entity = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
            if (entity is null || !entity.IsValid)
            {
                Log($"could not create custom_hud_layout for {panel.Layout}");
                return;
            }
            entity.StrLayout = panel.Layout;
            entity.DispatchSpawn();
            panel.Attach(entity);
        }
        catch (Exception e)
        {
            Log($"spawn of {panel.Layout} failed — {e.Message}");
        }
    }

    // ── engine events ────────────────────────────────────────────────────────────────────────────

    private static HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // The first safe moment on every map. Entities survive rounds, so this only fills in what is missing.
        WorldReady = true;
        SpawnAll();
        return HookResult.Continue;
    }

    private static void OnMapEnd()
    {
        // The map takes every entity with it; the next round start makes new ones.
        WorldReady = false;
        foreach (var panel in Panels) panel.Detach();
        Toasts.Reset();
        Votes.Reset();
    }

    private static void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!player.IsValid) return;
        foreach (var panel in Panels)
        {
            if (!panel.Owns(layout)) continue;
            try { panel.RaiseClick(player, buttonId); }
            catch (Exception e) { Log($"click handler for {panel.Layout} failed — {e.Message}"); }
            return;
        }
    }

    private static void OnPutInServer(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is null || !player.IsValid) return;
        foreach (var panel in Panels) panel.ResetFor(player);
    }

    private static void OnDisconnect(int slot)
    {
        foreach (var panel in Panels) panel.ForgetSlot(slot);
        Toasts.Forget(slot);
        Votes.Forget(slot);
    }

    // ── diagnostics ──────────────────────────────────────────────────────────────────────────────

    private static void OnDiagnostics(CCSPlayerController? caller, CommandInfo command)
    {
        command.ReplyToCommand($"CS2UIKit: world {(WorldReady ? "ready" : "not ready")}, {Panels.Count} panel(s)");
        foreach (var panel in Panels)
        {
            var entity = panel.Entity;
            var open = string.Join(", ", panel.OpenSlots.Select(s => Utilities.GetPlayerFromSlot(s)?.PlayerName ?? $"#{s}"));
            command.ReplyToCommand($"  {panel.Layout}: entity {(entity is null ? "none" : entity.Index.ToString())}, open for [{open}]");
        }
        var foreign = Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout")
            .Count(e => e.IsValid && !Panels.Any(p => p.Owns(e)));
        if (foreign > 0) command.ReplyToCommand($"  + {foreign} custom_hud_layout entit(ies) of other plugins");
    }
}
