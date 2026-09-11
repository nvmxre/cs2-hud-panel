using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using HudPanels;
using Microsoft.Extensions.Logging;

namespace HudPanels.Example;

/// <summary>
/// The smallest plugin that shows a clickable panel: type `!menu` in chat, click a row, done.
///
/// Everything this file does is drive state. The panel's shape — the rows, the labels, the styling —
/// lives in `hud/layout/example_menu.xml` and `hud/styles/example_menu.css`, which are compiled and
/// shipped to clients in a Workshop addon. See the README for that half.
/// </summary>
public sealed class ExamplePlugin : BasePlugin
{
    public override string ModuleName => "HudPanel Example";
    public override string ModuleVersion => "1.0.0";

    private const string Layout = "panorama/layout/custom_game/example_menu.xml";
    private const string Root = "example_root";

    private static readonly string[] Rows = { "Bandage", "Medkit", "Flashlight", "Ammo" };

    private HudPanel? _panel;

    public override void Load(bool hotReload)
    {
        _panel = new HudPanel(Layout, m => Logger.LogInformation("{Message}", m));
        _panel.Clicked += OnClicked;
        _panel.Start(this);
    }

    // Removing the panel on unload is not optional: leftovers accumulate and hold the player's cursor.
    public override void Unload(bool hotReload) => _panel?.Stop(this);

    [ConsoleCommand("css_menu", "Open the example panel")]
    public void OnMenu(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid || _panel is null) return;

        if (_panel.IsVisible(player.Slot))
        {
            _panel.Hide(player, Root);
            return;
        }

        _panel.SetText(player, "example_title", "Example menu");
        for (var i = 0; i < Rows.Length; i++)
            _panel.SetText(player, $"example_row_{i}_text", Rows[i]);

        _panel.Show(player, Root);
    }

    private void OnClicked(CCSPlayerController player, string buttonId)
    {
        if (buttonId == "example_close")
        {
            _panel?.Hide(player, Root);
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
