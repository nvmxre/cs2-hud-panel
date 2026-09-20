using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace HudPanels;

/// <summary>
/// Opens a <see cref="HudPanel"/> with the stock **B** key — no binds, no chat command.
///
/// How it works. A server cannot see the B key and cannot rebind it. But while the stock buy menu is
/// open, the client itself puts the class `HUD_BUYMENU_VISIBLE` on the HUD root, and your layout lives
/// inside that root. So the plugin never opens anything: it only marks the players who are *allowed*
/// the panel (a marker class on the window), and one stylesheet rule shows the window when both
/// classes are present:
///
///   .HUD_BUYMENU_VISIBLE .my-window.native { visibility: visible; opacity: 1; position: 0px 0px 0px; }
///
/// Three things have to hold for this to work, and the bridge takes care of all of them:
///   * stock buying stays ENABLED (`mp_buy_anywhere 1`, `mp_buytime 60000`). With buying disabled the
///     client opens nothing and sends nothing. The game-mode config resets both cvars every round, so
///     they are re-applied on round start;
///   * stock purchases are blocked at the command level — `buy`, `autobuy` and `rebuy` never reach the
///     engine. Otherwise the player buys through the stock menu that is open under yours;
///   * the cursor is captured while the stock menu is open. Hover works without capture (it is
///     client-side CSS); clicks do not. The client announces the menu with the `open_buymenu` and
///     `close_buymenu` commands, which DO reach the server as long as buying is enabled.
///
/// Two consequences for your code. The client shows the panel without asking the server, so the content
/// must be drawn BEFORE the key is pressed: draw in <see cref="Prepare"/>, which fires when a player is
/// granted the panel and again each time the stock menu opens. And the server cannot hide a panel the
/// class holds open: <see cref="Close"/> asks the client to close the stock menu instead.
///
/// A player who is not allowed the panel still gets the stock buy menu on B, with purchases blocked;
/// <see cref="SetBuying"/> with <c>false</c> closes it for everyone until the next round start.
///
/// This is what the buy menu in PROJECT ZERO runs on.
///
/// Usage:
/// <code>
/// _bridge = new BuyMenuBridge(_panel, "my_window", "my_dim");
/// _bridge.Prepare += player => Draw(player);
/// _bridge.Start(this, hotReload);
///
/// // whenever your rules change — alive humans may buy, everyone else may not:
/// _bridge.Allow(player, allowed);
/// </code>
/// </summary>
public sealed class BuyMenuBridge
{
    private static readonly string[] BuyCommands = { "buy", "autobuy", "rebuy" };

    private readonly HudPanel _panel;
    private readonly string _windowId;
    private readonly string? _dimId;
    private readonly string _markerClass;
    private readonly Action<string>? _log;

    /// <summary>Players who may have the panel on B (marker class is on their window).</summary>
    private readonly HashSet<int> _allowed = new();

    /// <summary>Players whose cursor we hold because their stock menu is open.</summary>
    private readonly HashSet<int> _captured = new();

    // Kept as fields so the very same delegates can be removed in Stop.
    private readonly CommandInfo.CommandListenerCallback _onOpen;
    private readonly CommandInfo.CommandListenerCallback _onClose;
    private readonly CommandInfo.CommandListenerCallback _onBuy;

    /// <summary>
    /// Draw the panel's content for this player now. Fires when <see cref="Allow"/> grants the panel and
    /// every time the stock menu opens — the client will show whatever is there without asking.
    /// </summary>
    public event Action<CCSPlayerController>? Prepare;

    /// <summary>The stock buy menu opened (true) or closed (false) for a player who is allowed the panel.</summary>
    public event Action<CCSPlayerController, bool>? StockMenuToggled;

    /// <summary>Answer `buy`, `autobuy` and `rebuy` with Handled, so the stock menu under yours sells nothing. Default true.</summary>
    public bool BlockStockPurchases { get; init; } = true;

    /// <summary>Keep `mp_buy_anywhere 1` and `mp_buytime 60000`, re-applied on every round start. Default true.</summary>
    public bool KeepBuyingEnabled { get; init; } = true;

    /// <param name="panel">The panel to open.</param>
    /// <param name="windowPanelId">Id of the window panel — the one your `.HUD_BUYMENU_VISIBLE` rule targets.</param>
    /// <param name="dimPanelId">Optional backdrop panel that gets the same marker class.</param>
    /// <param name="markerClass">The class that means "this player may have the panel". Default `native`.</param>
    /// <param name="log">Optional sink for diagnostics.</param>
    public BuyMenuBridge(HudPanel panel, string windowPanelId, string? dimPanelId = null,
        string markerClass = "native", Action<string>? log = null)
    {
        _panel = panel;
        _windowId = windowPanelId;
        _dimId = dimPanelId;
        _markerClass = markerClass;
        _log = log;

        _onOpen = (player, _) => OnStockMenu(player, true);
        _onClose = (player, _) => OnStockMenu(player, false);
        _onBuy = (player, _) => player is null ? HookResult.Continue : HookResult.Handled;
    }

    /// <summary>May this player open the panel with B right now.</summary>
    public bool IsAllowed(int playerSlot) => _allowed.Contains(playerSlot);

    /// <summary>Is this player's stock buy menu open, as far as the client has told us.</summary>
    public bool IsStockMenuOpen(int playerSlot) => _captured.Contains(playerSlot);

    /// <summary>Call from <c>Load</c>, after the panel's own <c>Start</c>, with the same <c>hotReload</c> flag.</summary>
    public void Start(BasePlugin plugin, bool hotReload)
    {
        plugin.AddCommandListener("open_buymenu", _onOpen);
        plugin.AddCommandListener("close_buymenu", _onClose);
        if (BlockStockPurchases)
            foreach (var command in BuyCommands) plugin.AddCommandListener(command, _onBuy);

        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterListener<Listeners.OnClientDisconnect>(OnDisconnect);

        // On a hot reload the map is running and the game-mode config has already had its say.
        if (hotReload && KeepBuyingEnabled) ApplyCvars();
    }

    /// <summary>Call from <c>Unload</c>, before the panel's own <c>Stop</c>.</summary>
    public void Stop(BasePlugin plugin)
    {
        plugin.RemoveCommandListener("open_buymenu", _onOpen, HookMode.Pre);
        plugin.RemoveCommandListener("close_buymenu", _onClose, HookMode.Pre);
        if (BlockStockPurchases)
            foreach (var command in BuyCommands) plugin.RemoveCommandListener(command, _onBuy, HookMode.Pre);

        plugin.DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RemoveListener<Listeners.OnClientDisconnect>(OnDisconnect);

        foreach (var slot in _allowed.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not null && player.IsValid) Allow(player, false);
        }
        _allowed.Clear();
        _captured.Clear();
    }

    /// <summary>
    /// Grant or revoke the panel on B. Idempotent: call it as often as your rules change (spawn, death,
    /// a phase ending). Granting fires <see cref="Prepare"/> so the content is ready before the key.
    /// </summary>
    public void Allow(CCSPlayerController player, bool allow)
    {
        if (!player.IsValid || player.IsBot) return;

        if (allow)
        {
            if (!_panel.EnsureSpawned(player)) return;
            if (!_allowed.Add(player.Slot)) return;
            Mark(player, true);
            Prepare?.Invoke(player);
            _log?.Invoke($"BuyMenuBridge: B granted to {player.PlayerName}");
            return;
        }

        if (!_allowed.Remove(player.Slot)) return;
        Mark(player, false);
        Capture(player, false);
        _log?.Invoke($"BuyMenuBridge: B revoked from {player.PlayerName}");
    }

    /// <summary>
    /// Close the panel that B opened. The server cannot hide it — the client's class holds it — so this
    /// asks the client to close the stock buy menu, and the panel goes with it in the same frame.
    /// </summary>
    public void Close(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        Capture(player, false);
        player.ExecuteClientCommand("close_buymenu");
    }

    /// <summary>
    /// Turn stock buying on or off for everyone. Off closes every open buy menu on the next frame and B
    /// opens nothing until it is back on — the way to end a "shopping phase". Round start re-applies the
    /// value from <see cref="KeepBuyingEnabled"/>.
    /// </summary>
    public void SetBuying(bool on) => Server.ExecuteCommand(on ? "mp_buytime 60000" : "mp_buytime 0");

    /// <summary>
    /// Optional safety net for a periodic timer. `CCSPlayerPawn.IsBuyMenuOpen` flickers between reads
    /// while the menu is plainly open, so it is only ever used to switch capture ON — never off. Release
    /// is left to `close_buymenu`, <see cref="Allow"/> and the round start.
    /// </summary>
    public void Poll()
    {
        foreach (var slot in _allowed)
        {
            if (_captured.Contains(slot)) continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) continue;

            bool open;
            try { open = pawn.IsBuyMenuOpen; }
            catch { continue; }   // the read itself can throw during map transitions

            if (open) OnStockMenu(player, true);
        }
    }

    private HookResult OnStockMenu(CCSPlayerController? player, bool open)
    {
        if (player is null || !player.IsValid || player.IsBot) return HookResult.Continue;
        // Opened by HudPanel.Show — that capture belongs to the panel, not to us.
        if (_panel.IsVisible(player.Slot)) return HookResult.Continue;
        if (open && !_allowed.Contains(player.Slot)) return HookResult.Continue;
        if (open == _captured.Contains(player.Slot)) return HookResult.Continue;

        Capture(player, open);
        if (open) Prepare?.Invoke(player);   // prices and state as of this very moment
        StockMenuToggled?.Invoke(player, open);
        return HookResult.Continue;
    }

    private void Mark(CCSPlayerController player, bool on)
    {
        _panel.SetClass(player, _windowId, _markerClass, on);
        if (_dimId is not null) _panel.SetClass(player, _dimId, _markerClass, on);
    }

    private void Capture(CCSPlayerController player, bool on)
    {
        if (on ? !_captured.Add(player.Slot) : !_captured.Remove(player.Slot)) return;
        _panel.CaptureInput(player, on);
        _log?.Invoke($"BuyMenuBridge: stock menu {(on ? "open" : "closed")} for {player.PlayerName}, capture {(on ? "on" : "off")}");
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (KeepBuyingEnabled) ApplyCvars();

        // The client drops its menu on a new round; drop the capture with it.
        foreach (var slot in _captured.ToList())
        {
            _captured.Remove(slot);
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid || _panel.IsVisible(slot)) continue;
            _panel.CaptureInput(player, false);
        }
        return HookResult.Continue;
    }

    private void OnDisconnect(int playerSlot)
    {
        // Slots are reused: the next occupant must not inherit the grant.
        _allowed.Remove(playerSlot);
        _captured.Remove(playerSlot);
    }

    private static void ApplyCvars()
    {
        Server.ExecuteCommand("mp_buy_anywhere 1");
        Server.ExecuteCommand("mp_buytime 60000");
    }
}
