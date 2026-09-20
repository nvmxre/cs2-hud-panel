using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using HudPanels;
using Microsoft.Extensions.Logging;

namespace HudPanels.Example;

/// <summary>
/// The smallest plugin that shows a clickable panel, two ways: type `!menu` in chat, or press B.
///
/// Everything this file does is drive state. The panel's shape — the rows, the labels, the styling —
/// lives in `hud/layout/example_menu.xml` and `hud/styles/example_menu.css`, which are compiled and
/// shipped to clients in a Workshop addon. See the README for that half.
/// </summary>
public sealed class ExamplePlugin : BasePlugin
{
    public override string ModuleName => "HudPanel Example";
    public override string ModuleVersion => "1.1.0";

    private const string Layout = "panorama/layout/custom_game/example_menu.xml";
    private const string Root = "example_root";
    private const string Dim = "example_dim";

    private static readonly string[] Rows = { "Bandage", "Medkit", "Flashlight", "Ammo" };

    private HudPanel? _panel;
    private BuyMenuBridge? _bridge;

    public override void Load(bool hotReload)
    {
        _panel = new HudPanel(Layout, m => Logger.LogInformation("{Message}", m));
        _panel.Clicked += OnClicked;
        _panel.Start(this, hotReload);

        // The same panel on the stock B key. The bridge keeps buying enabled, blocks stock purchases and
        // captures the cursor while the stock menu is open; this plugin only decides who may have it.
        _bridge = new BuyMenuBridge(_panel, Root, Dim, log: m => Logger.LogInformation("{Message}", m));
        _bridge.Prepare += Draw;   // the client opens the panel on its own — the content must already be there
        _bridge.Start(this, hotReload);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        // Optional safety net: the pawn's IsBuyMenuOpen flag flickers, so it may only ever switch capture ON.
        AddTimer(0.5f, () => _bridge?.Poll(), TimerFlags.REPEAT);
    }

    // Removing the panel on unload is not optional: leftovers accumulate and hold the player's cursor.
    public override void Unload(bool hotReload)
    {
        _bridge?.Stop(this);
        _panel?.Stop(this);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid || player.IsBot || _panel is null) return HookResult.Continue;

        // Slots are reused: forget whatever the previous occupant left open, then grant the key.
        _panel.SetClass(player, Dim, "shown", false);
        _panel.Hide(player, Root);
        _bridge?.Allow(player, true);
        return HookResult.Continue;
    }

    [ConsoleCommand("css_menu", "Open the example panel")]
    public void OnMenu(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid || _panel is null) return;

        if (_panel.IsVisible(player.Slot))
        {
            Close(player);
            return;
        }

        Draw(player);
        _panel.SetClass(player, Dim, "shown", true);
        _panel.Show(player, Root);
    }

    /// <summary>Fill the panel for one player: before !menu shows it, and — through the bridge — before B can.</summary>
    private void Draw(CCSPlayerController player)
    {
        if (_panel is null) return;

        _panel.SetText(player, "example_title", "Example menu");
        for (var i = 0; i < Rows.Length; i++)
        {
            _panel.SetText(player, $"example_row_{i}_text", Rows[i]);
            _panel.SetClass(player, $"example_row_{i}", "picked", false);   // clear the last visit's highlight
        }
    }

    private void Close(CCSPlayerController player)
    {
        if (_panel is null) return;

        if (_panel.IsVisible(player.Slot))
        {
            // Opened with !menu: ours to close.
            _panel.SetClass(player, Dim, "shown", false);
            _panel.Hide(player, Root);
        }
        else
        {
            // Opened with B: the client holds it, so ask the client to close the stock menu.
            _bridge?.Close(player);
        }
    }

    private void OnClicked(CCSPlayerController player, string buttonId)
    {
        if (buttonId == "example_close")
        {
            Close(player);
            return;
        }

        // Rows are named example_row_0 … example_row_3 in the layout.
        if (!buttonId.StartsWith("example_row_", StringComparison.Ordinal)) return;
        if (!int.TryParse(buttonId.AsSpan("example_row_".Length), out var index)) return;
        if (index < 0 || index >= Rows.Length) return;

        player.PrintToChat($" You picked {Rows[index]}");

        // Give the row a moment of feedback, then let the player keep browsing.
        _panel?.SetClass(player, buttonId, "picked", true);
    }
}
