using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;

namespace HudPanels;

/// <summary>
/// A clickable, flicker-free HUD panel for CounterStrikeSharp, built on the `custom_hud_layout`
/// entity that Valve added to CS2 on 24 August 2026.
///
/// Why this exists. Before `custom_hud_layout` a plugin had two bad options for on-screen UI:
///   * `PrintToCenter` — plain text only, and the font shrinks as you add lines;
///   * `PrintToCenterHtml` — colours and sizes, but the panel re-animates roughly once a second,
///     so anything permanent visibly flickers.
/// Neither can react to a mouse click, so menus were built on number keys — which requires
/// rebinding the player's keyboard, and a server is not allowed to do that (see docs/GOTCHAS.md).
///
/// `custom_hud_layout` solves all of it: a real Panorama panel, per-player state, and clicks
/// delivered straight to your plugin with the id of the button that was pressed.
///
/// How it works. The *structure* lives in a Panorama layout (XML + CSS) that ships to clients in a
/// Workshop addon. This class drives the *state*: it fills text through dialog variables, toggles
/// CSS classes, captures the cursor, and routes clicks back to you. One entity serves every player,
/// but each of them sees their own text and their own classes.
///
/// Minimal usage:
/// <code>
/// _panel = new HudPanel("panorama/layout/custom_game/my_menu.xml", Logger.LogInformation);
/// _panel.Clicked += (player, buttonId) => player.PrintToChat($"clicked {buttonId}");
/// _panel.Start(this);
///
/// _panel.SetText(player, "title", "Hello");
/// _panel.Show(player);
/// </code>
/// </summary>
public sealed class HudPanel
{
    private readonly string _layoutResource;
    private readonly Action<string>? _log;

    private CCSCustomHudLayout? _entity;
    private readonly HashSet<int> _visible = new();

    /// <summary>A player clicked a Button in the layout. The argument is that button's `id`.</summary>
    public event Action<CCSPlayerController, string>? Clicked;

    /// <param name="layoutResource">
    /// Full path to the layout **source**, extension included:
    /// `panorama/layout/custom_game/my_menu.xml`. Not the compiled `.vxml_c`, and not a bare name —
    /// with a wrong value the entity still spawns and everything looks fine server-side, but the
    /// client silently renders nothing.
    /// </param>
    /// <param name="log">Optional sink for diagnostics.</param>
    public HudPanel(string layoutResource, Action<string>? log = null)
    {
        _layoutResource = layoutResource;
        _log = log;
    }

    /// <summary>Is the panel currently shown to this player.</summary>
    public bool IsVisible(int playerSlot) => _visible.Contains(playerSlot);

    public void Start(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnCustomHudClicked>(OnClicked);
        // Entities do not survive a map change, so the panel is recreated on every map.
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Server.NextWorldUpdate(Spawn));
        Spawn();
    }

    /// <summary>
    /// Always call this from your plugin's <c>Unload</c>.
    ///
    /// If you do not, the entity outlives the plugin and the next load creates another one next to it.
    /// They pile up, every copy keeps its own input capture and its own per-player visibility, and the
    /// symptoms are baffling: a cursor that nothing releases, panels that appear on their own, commands
    /// that seem to reach one panel while another is on screen.
    /// </summary>
    public void Stop(BasePlugin plugin)
    {
        plugin.RemoveListener<Listeners.OnCustomHudClicked>(OnClicked);
        HideAll();

        if (_entity is not null && _entity.IsValid) _entity.Remove();
        _entity = null;
    }

    private void Spawn()
    {
        if (_entity is not null && _entity.IsValid) return;

        try
        {
            // Clean up anything left behind by a previous load or a server crash.
            var orphans = 0;
            foreach (var old in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
            {
                if (!old.IsValid) continue;
                old.Remove();
                orphans++;
            }
            if (orphans > 0) _log?.Invoke($"HudPanel: removed {orphans} orphaned panel(s)");

            var entity = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
            if (entity is null || !entity.IsValid)
            {
                _log?.Invoke("HudPanel: could not create custom_hud_layout");
                return;
            }

            entity.StrLayout = _layoutResource;
            entity.DispatchSpawn();
            _entity = entity;
            _log?.Invoke($"HudPanel: ready, layout = {_layoutResource}");
        }
        catch (Exception e)
        {
            _log?.Invoke($"HudPanel: spawn failed — {e.Message}");
        }
    }

    /// <summary>
    /// Set the text of one panel. The layout must bind it, e.g. `&lt;Label id="title" text="{s:text}" /&gt;`.
    /// Text is per-player: two players can read different things from the same panel.
    /// </summary>
    public void SetText(CCSPlayerController player, string panelId, string value, string variable = "text")
    {
        if (_entity is null || !_entity.IsValid || !player.IsValid) return;
        _entity.SetDialogVariableStringForPlayer(player, panelId, variable, value ?? string.Empty);
    }

    /// <summary>
    /// Add or remove a CSS class on one panel, for this player only. This is how you show selection,
    /// disabled state, colours — anything the stylesheet can express.
    /// </summary>
    public void SetClass(CCSPlayerController player, string panelId, string className, bool has)
    {
        if (_entity is null || !_entity.IsValid || !player.IsValid) return;
        _entity.SetHasClassForPlayer(player, panelId, className, has);
    }

    /// <summary>
    /// Show the panel: adds <paramref name="visibleClass"/> to the root panel id and gives the player
    /// a cursor. Your stylesheet decides what that class means.
    /// </summary>
    public void Show(CCSPlayerController player, string rootPanelId, string visibleClass = "shown")
    {
        if (_entity is null || !_entity.IsValid) Spawn();
        if (_entity is null || !_entity.IsValid || !player.IsValid || player.IsBot) return;

        _visible.Add(player.Slot);
        SetClass(player, rootPanelId, visibleClass, true);
        _entity.SetInputCaptureEnabled(player, true);
    }

    /// <summary>Hide the panel and release the cursor.</summary>
    public void Hide(CCSPlayerController player, string rootPanelId, string visibleClass = "shown")
    {
        if (_entity is null || !_entity.IsValid || !player.IsValid) return;

        _visible.Remove(player.Slot);
        SetClass(player, rootPanelId, visibleClass, false);
        _entity.SetInputCaptureEnabled(player, false);
    }

    /// <summary>
    /// Release every player's cursor. Per-player state is stored by slot and outlives a disconnect,
    /// so a new player can inherit a panel somebody else left open — call this on round start and
    /// on spawn if that matters to you.
    /// </summary>
    public void HideAll()
    {
        foreach (var slot in _visible.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            _visible.Remove(slot);
            if (player is null || !player.IsValid || _entity is null || !_entity.IsValid) continue;
            _entity.SetInputCaptureEnabled(player, false);
        }
    }

    private void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (player.IsValid) Clicked?.Invoke(player, buttonId);
    }
}
