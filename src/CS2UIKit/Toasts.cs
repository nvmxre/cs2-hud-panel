using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace CS2UIKit;

/// <summary>Colour and icon of a toast. Mirrors CS2's own main-menu notices.</summary>
public enum ToastStyle
{
    /// <summary>Blue, "i".</summary>
    Info,
    /// <summary>Green, check mark.</summary>
    Success,
    /// <summary>Yellow, exclamation mark — the look of CS2's own cooldown notice.</summary>
    Warning,
    /// <summary>Red, cross.</summary>
    Danger,
    /// <summary>Grey, dot.</summary>
    Neutral,
}

/// <summary>Where the toast stack sits on screen.</summary>
public enum ToastPosition { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>
/// Toast notifications in the style of CS2's main-menu notices: a dark card with the map-line texture, a coloured
/// icon square, an accent bar, a bold title, an optional message and link. Up to four are stacked per player,
/// newest on top, each fading out on its own timer. They never take the cursor, so a notice cannot interrupt aiming.
///
/// <code>
/// Toasts.Show(player, "Round started", "One of you is already infected.", ToastStyle.Warning);
/// Toasts.ShowAll("Airdrop incoming", "Crates land in 10 seconds.", ToastStyle.Info, seconds: 8);
/// </code>
///
/// Needs the CS2UIKit Workshop addon on clients (the layout ships there).
/// </summary>
public static class Toasts
{
    public const string Layout = "panorama/layout/custom_game/cs2uikit_toasts.xml";

    /// <summary>Cards visible at once per player. The layout has exactly this many slots.</summary>
    public const int Slots = 4;

    /// <summary>Default time on screen, seconds.</summary>
    public static float DefaultSeconds { get; set; } = 6f;

    /// <summary>Where the stack sits. Applied the next time a toast is shown to a player.</summary>
    public static ToastPosition Position { get; set; } = ToastPosition.TopRight;

    private sealed record Card(string Title, string Message, string Link, ToastStyle Style, double Until);

    private static Panel? _panel;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _timer;
    private static readonly Dictionary<int, List<Card>> Stacks = new();

    public static void Show(CCSPlayerController player, string title, string? message = null,
        ToastStyle style = ToastStyle.Info, float? seconds = null, string? link = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var panel = EnsurePanel();

        if (!Stacks.TryGetValue(player.Slot, out var stack)) Stacks[player.Slot] = stack = new List<Card>();
        stack.Insert(0, new Card(title, message ?? string.Empty, link ?? string.Empty, style,
            Server.CurrentTime + Math.Max(1f, seconds ?? DefaultSeconds)));
        if (stack.Count > Slots) stack.RemoveRange(Slots, stack.Count - Slots);

        if (!panel.IsOpen(player)) panel.Show(player);
        panel.SetVariant(player, "toasts", "pos-", PositionClass(Position));
        Render(player, stack);
    }

    /// <summary>Show the same toast to every human on the server.</summary>
    public static void ShowAll(string title, string? message = null, ToastStyle style = ToastStyle.Info,
        float? seconds = null, string? link = null)
    {
        foreach (var player in Utilities.GetPlayers())
            Show(player, title, message, style, seconds, link);
    }

    /// <summary>Remove every toast of a player at once.</summary>
    public static void Clear(CCSPlayerController player)
    {
        if (!Stacks.Remove(player.Slot) || _panel is null) return;
        Render(player, new List<Card>());
        _panel.Hide(player);
    }

    private static Panel EnsurePanel()
    {
        if (_panel is not null) return _panel;
        _panel = new Panel(Layout, new PanelOptions { Root = "toasts", CaptureInput = false });
        _timer = UIKit.Plugin.AddTimer(0.25f, Tick, TimerFlags.REPEAT);
        return _panel;
    }

    /// <summary>
    /// Fill the slots from the stack: slot 0 is the newest. The layout has no way to insert a card, so a new toast
    /// re-fills every slot below it — cards "move down" by getting their neighbour's text.
    /// </summary>
    private static void Render(CCSPlayerController player, List<Card> stack)
    {
        var panel = _panel!;
        for (var i = 0; i < Slots; i++)
        {
            var id = $"t{i}";
            if (i >= stack.Count)
            {
                panel.SetClass(player, id, "on", false);
                continue;
            }
            var card = stack[i];
            panel.SetText(player, id + "_title", card.Title);
            panel.SetText(player, id + "_msg", card.Message);
            panel.SetText(player, id + "_link", card.Link);
            panel.SetClass(player, id, "has-msg", card.Message.Length > 0);
            panel.SetClass(player, id, "has-link", card.Link.Length > 0);
            panel.SetVariant(player, id, "style-", card.Style.ToString().ToLowerInvariant());
            panel.SetClass(player, id, "on", true);
        }
    }

    private static void Tick()
    {
        if (_panel is null || Stacks.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var (slot, stack) in Stacks.ToList())
        {
            if (stack.RemoveAll(c => c.Until <= now) == 0) continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid)
            {
                Stacks.Remove(slot);
                continue;
            }
            Render(player, stack);
            if (stack.Count == 0)
            {
                Stacks.Remove(slot);
                _panel.Hide(player);
            }
        }
    }

    private static string PositionClass(ToastPosition position) => position switch
    {
        ToastPosition.TopLeft => "top-left",
        ToastPosition.BottomRight => "bottom-right",
        ToastPosition.BottomLeft => "bottom-left",
        _ => "top-right",
    };

    // ── called by UIKit ──────────────────────────────────────────────────────────────────────────

    internal static void Forget(int slot) => Stacks.Remove(slot);

    /// <summary>Map change: the entity is gone and so is every card on screen.</summary>
    internal static void Reset() => Stacks.Clear();

    internal static void Stop()
    {
        _timer?.Kill();
        _timer = null;
        _panel = null;
        Stacks.Clear();
    }
}
