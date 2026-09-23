using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;

namespace CS2UIKit;

/// <summary>Parts of the stock HUD a panel can hide while it is open (<c>CBasePlayerPawn::m_iHideHUD</c> bits).</summary>
[Flags]
public enum HideHud : uint
{
    None = 0,
    /// <summary>Ammo and weapon selection.</summary>
    Weapons = 1u << 0,
    /// <summary>Everything except money.</summary>
    All = 1u << 2,
    /// <summary>Health and armour.</summary>
    Health = 1u << 3,
    /// <summary>Kill feed, pickups and similar notices.</summary>
    Notices = 1u << 6,
    Chat = 1u << 7,
    Crosshair = 1u << 8,
    Radar = 1u << 12,
}

/// <summary>How a panel behaves. The defaults fit a menu: it takes the cursor.</summary>
public sealed class PanelOptions
{
    /// <summary>Id of the panel that <see cref="Panel.Show"/> toggles. Not the layout root: the root may not have an id.</summary>
    public string Root { get; init; } = "root";

    /// <summary>Class that makes <see cref="Root"/> visible. Your stylesheet decides what it means.</summary>
    public string ShownClass { get; init; } = "shown";

    /// <summary>Id of a full-screen backdrop toggled together with the window, or null.</summary>
    public string? Backdrop { get; init; }

    /// <summary>
    /// Take the cursor while shown. Menus need it — clicks only reach the server under capture. Anything the
    /// player only reads (toasts, timers) must leave it off, or a notification pulls up a cursor mid-fight.
    /// </summary>
    public bool CaptureInput { get; init; } = true;

    /// <summary>Stock HUD parts hidden while the panel is shown, restored when it closes.</summary>
    public HideHud HideHud { get; init; }
}

/// <summary>A button in a panel was clicked.</summary>
public sealed record PanelClick(CCSPlayerController Player, string ButtonId);

/// <summary>
/// One Panorama layout on screen: a <c>custom_hud_layout</c> entity with per-player text, classes and cursor.
///
/// The structure lives in the layout (XML + CSS), shipped to clients in a Workshop addon. The panel drives the
/// state: it fills <c>{s:text}</c> through dialog variables, toggles CSS classes and captures the cursor, per
/// player. Several panels can be live at once; each click comes back to the panel that was clicked.
///
/// <code>
/// var panel = new Panel("panorama/layout/custom_game/my_menu.xml", new PanelOptions { Root = "my_root" });
/// panel.Clicked += c => c.Player.PrintToChat($"clicked {c.ButtonId}");
/// panel.SetText(player, "my_title", "Hello");
/// panel.Show(player);
/// </code>
///
/// Create panels after <see cref="UIKit.Init"/>. UIKit creates the entity at the first safe moment of every map
/// and recreates it after a map change, so a panel object is made once in <c>Load</c> and kept.
/// </summary>
public sealed class Panel
{
    /// <summary>Layout source path, extension included: <c>panorama/layout/custom_game/my_menu.xml</c>.</summary>
    public string Layout { get; }

    public PanelOptions Options { get; }

    /// <summary>A button in this panel was clicked. The argument carries the button's <c>id</c>.</summary>
    public event Action<PanelClick>? Clicked;

    /// <summary>The panel was closed for a player through <see cref="Hide"/>.</summary>
    public event Action<CCSPlayerController>? Closed;

    private CCSCustomHudLayout? _entity;
    private readonly HashSet<int> _open = new();

    /// <summary>HUD bits we switched on, per slot, so closing restores exactly what we changed.</summary>
    private readonly Dictionary<int, uint> _hudAdded = new();

    /// <summary>Current class of each variant group, per slot and panel (see <see cref="SetVariant"/>).</summary>
    private readonly Dictionary<(int Slot, string Panel, string Prefix), string> _variants = new();

    /// <param name="layout">
    /// Full path to the layout <b>source</b>, extension included. Not the compiled <c>.vxml_c</c>, not a bare
    /// name: with a wrong value the entity still spawns and everything looks fine server-side, but the client
    /// silently renders nothing.
    /// </param>
    public Panel(string layout, PanelOptions? options = null)
    {
        Layout = layout;
        Options = options ?? new PanelOptions();
        UIKit.Register(this);
    }

    /// <summary>The entity, or null while it does not exist (between maps, before the first round).</summary>
    public CCSCustomHudLayout? Entity => _entity is not null && _entity.IsValid ? _entity : null;

    public bool IsOpen(CCSPlayerController player) => player.IsValid && _open.Contains(player.Slot);

    /// <summary>Slots that have the panel open.</summary>
    public IReadOnlyCollection<int> OpenSlots => _open;

    // ── state ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set the text of one panel for one player. The layout binds it: <c>&lt;Label id="title" text="{s:text}" /&gt;</c>.
    /// </summary>
    public void SetText(CCSPlayerController player, string panelId, string value, string variable = "text")
    {
        if (!Ready(player)) return;
        _entity!.SetDialogVariableStringForPlayer(player, panelId, variable, value ?? string.Empty);
    }

    /// <summary>Add or remove a CSS class on one panel, for one player: selection, disabled rows, colours.</summary>
    public void SetClass(CCSPlayerController player, string panelId, string className, bool has)
    {
        if (!Ready(player)) return;
        _entity!.SetHasClassForPlayer(player, panelId, className, has);
    }

    /// <summary>
    /// Switch between mutually exclusive classes on one panel: <c>SetVariant(p, "toast_0", "accent-", "yellow")</c>
    /// removes the <c>accent-*</c> class it set before and adds <c>accent-yellow</c>. Colours, widths and positions the
    /// server cannot send directly are done this way. Null removes the group.
    /// </summary>
    public void SetVariant(CCSPlayerController player, string panelId, string prefix, string? value)
    {
        if (!Ready(player)) return;
        var key = (player.Slot, panelId, prefix);
        if (_variants.TryGetValue(key, out var old))
        {
            if (old == value) return;
            _entity!.SetHasClassForPlayer(player, panelId, prefix + old, false);
        }
        if (value is null)
        {
            _variants.Remove(key);
            return;
        }
        _variants[key] = value;
        _entity!.SetHasClassForPlayer(player, panelId, prefix + value, true);
    }

    /// <summary>
    /// Give or take the cursor without touching visibility. The B-key bridge needs this: the client shows the
    /// panel on its own while the stock buy menu is open, but clicks reach the server only under capture.
    /// </summary>
    public void CaptureInput(CCSPlayerController player, bool on)
    {
        if (!Ready(player)) return;
        _entity!.SetInputCaptureEnabled(player, on);
    }

    // ── open / close ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Show the panel to a player: root and backdrop get the shown class, the cursor and HUD follow the options.</summary>
    public void Show(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        UIKit.EnsureWorld(player);
        if (!Ready(player)) return;

        _open.Add(player.Slot);
        SetClass(player, Options.Root, Options.ShownClass, true);
        if (Options.Backdrop is not null) SetClass(player, Options.Backdrop, Options.ShownClass, true);
        if (Options.CaptureInput) _entity!.SetInputCaptureEnabled(player, true);
        ApplyHideHud(player, true);
    }

    /// <summary>Hide the panel and release the cursor.</summary>
    public void Hide(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        var wasOpen = _open.Remove(player.Slot);
        if (Ready(player))
        {
            SetClass(player, Options.Root, Options.ShownClass, false);
            if (Options.Backdrop is not null) SetClass(player, Options.Backdrop, Options.ShownClass, false);
            if (Options.CaptureInput) _entity!.SetInputCaptureEnabled(player, false);
        }
        ApplyHideHud(player, false);
        if (wasOpen) Closed?.Invoke(player);
    }

    /// <summary>Hide the panel for everyone who has it open.</summary>
    public void HideAll()
    {
        foreach (var slot in _open.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not null && player.IsValid) Hide(player);
            else _open.Remove(slot);
        }
    }

    private void ApplyHideHud(CCSPlayerController player, bool on)
    {
        if (Options.HideHud == HideHud.None) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;

        if (on)
        {
            // Only bits that were off: closing must not switch on something another plugin hid on purpose.
            var add = (uint)Options.HideHud & ~pawn.HideHUD;
            if (add == 0) return;
            _hudAdded[player.Slot] = add;
            pawn.HideHUD |= add;
        }
        else
        {
            if (!_hudAdded.Remove(player.Slot, out var added)) return;
            pawn.HideHUD &= ~added;
        }
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_iHideHUD");
    }

    private bool Ready(CCSPlayerController player) =>
        player.IsValid && _entity is not null && _entity.IsValid;

    // ── called by UIKit ──────────────────────────────────────────────────────────────────────────

    internal bool Owns(CCSCustomHudLayout layout) =>
        _entity is not null && _entity.IsValid && layout.IsValid && layout.Index == _entity.Index;

    internal void RaiseClick(CCSPlayerController player, string buttonId) =>
        Clicked?.Invoke(new PanelClick(player, buttonId));

    internal bool HasEntity => _entity is not null && _entity.IsValid;

    internal void Attach(CCSCustomHudLayout entity) => _entity = entity;

    /// <summary>The map took the entity with it: forget everyone's state, nobody has the panel open any more.</summary>
    internal void Detach()
    {
        _entity = null;
        _open.Clear();
        _hudAdded.Clear();
        _variants.Clear();
    }

    /// <summary>
    /// A new player took the slot. Per-player state on the entity is stored by slot and outlives a disconnect, so
    /// the newcomer would inherit a panel someone else left open — with a captured cursor nothing releases.
    /// </summary>
    internal void ResetFor(CCSPlayerController player)
    {
        _open.Remove(player.Slot);
        _hudAdded.Remove(player.Slot);
        foreach (var key in _variants.Keys.Where(k => k.Slot == player.Slot).ToList())
        {
            if (Ready(player)) _entity!.SetHasClassForPlayer(player, key.Panel, key.Prefix + _variants[key], false);
            _variants.Remove(key);
        }
        if (!Ready(player)) return;
        SetClass(player, Options.Root, Options.ShownClass, false);
        if (Options.Backdrop is not null) SetClass(player, Options.Backdrop, Options.ShownClass, false);
        _entity!.SetInputCaptureEnabled(player, false);
    }

    /// <summary>The player left: drop our own bookkeeping for the slot (the entity is cleared when the next one joins).</summary>
    internal void ForgetSlot(int slot)
    {
        _open.Remove(slot);
        _hudAdded.Remove(slot);
    }
}
