using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2UIKit;

/// <summary>Colour and icon of a toast.</summary>
public enum ToastStyle
{
    /// <summary>Blue, "i".</summary>
    Info,
    /// <summary>Green, check mark.</summary>
    Success,
    /// <summary>Yellow, exclamation mark.</summary>
    Warning,
    /// <summary>Red, cross.</summary>
    Danger,
    /// <summary>Grey, dot.</summary>
    Neutral,
}

/// <summary>Where the toast stack sits on screen.</summary>
public enum ToastPosition { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>
/// Toast notifications: a dark rounded card with a soft wash of the style colour, a round icon, a bold title, an
/// optional message and link. Up to four are stacked per player, newest on top; each slides in, stays for its time
/// and slides out. They never take the cursor, so a notice cannot interrupt aiming, and play a short UI sound.
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

    /// <summary>
    /// Sound event played to the player when a toast appears; null or empty for silence. Stock CS2 UI events work
    /// without shipping anything: <c>HudChat.Message</c>, <c>UIPanorama.submenu_leveloptions_slidein</c>,
    /// <c>UI.PlayerPing</c>. <see cref="StyleSounds"/> overrides it per style.
    /// </summary>
    public static string? Sound { get; set; } = "HudChat.Message";

    /// <summary>Per-style sound, e.g. a sharper one for <see cref="ToastStyle.Danger"/>.</summary>
    public static Dictionary<ToastStyle, string> StyleSounds { get; } = new();

    public static float SoundVolume { get; set; } = 1f;

    /// <summary>Length of the slide-out, seconds. Matches the transition in the stylesheet.</summary>
    private const double LeaveSeconds = 0.3;

    private sealed class Card
    {
        public required string Title;
        public required string Message;
        public required string Link;
        public required ToastStyle Style;
        public required double Until;
        public bool Leaving;
    }

    private static Panel? _panel;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _timer;
    private static readonly Dictionary<int, List<Card>> Stacks = new();

    public static void Show(CCSPlayerController player, string title, string? message = null,
        ToastStyle style = ToastStyle.Info, float? seconds = null, string? link = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var panel = EnsurePanel();

        if (!Stacks.TryGetValue(player.Slot, out var stack)) Stacks[player.Slot] = stack = new List<Card>();
        stack.Insert(0, new Card
        {
            Title = title,
            Message = message ?? string.Empty,
            Link = link ?? string.Empty,
            Style = style,
            Until = Server.CurrentTime + Math.Max(1f, seconds ?? DefaultSeconds),
        });
        if (stack.Count > Slots) stack.RemoveRange(Slots, stack.Count - Slots);

        PlaySound(player, style);

        var fresh = !panel.IsOpen(player);
        if (fresh)
        {
            panel.Show(player);
            panel.SetVariant(player, "toasts", "pos-", PositionClass(Position));
            // The stack appears in this very frame. A card switched on in the same frame skips its transition —
            // the first toast would pop in while the next ones slide. One short beat lets the stack settle first.
            var slot = player.Slot;
            UIKit.Plugin.AddTimer(0.06f, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not null && p.IsValid && Stacks.TryGetValue(slot, out var s)) Render(p, s);
            });
            return;
        }

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
        _timer = UIKit.Plugin.AddTimer(0.1f, Tick, TimerFlags.REPEAT);
        return _panel;
    }

    private static void PlaySound(CCSPlayerController player, ToastStyle style)
    {
        var sound = StyleSounds.TryGetValue(style, out var own) ? own : Sound;
        if (string.IsNullOrWhiteSpace(sound)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;
        try { pawn.EmitSound(sound, new RecipientFilter(player), SoundVolume); }
        catch { /* a missing sound is never worth a broken notice */ }
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
                panel.SetClass(player, id, "leaving", false);
                continue;
            }
            var card = stack[i];
            panel.SetText(player, id + "_title", card.Title);
            panel.SetText(player, id + "_msg", card.Message);
            panel.SetText(player, id + "_link", card.Link);
            panel.SetClass(player, id, "has-msg", card.Message.Length > 0);
            panel.SetClass(player, id, "has-link", card.Link.Length > 0);
            panel.SetVariant(player, id, "style-", card.Style.ToString().ToLowerInvariant());
            panel.SetClass(player, id, "leaving", card.Leaving);
            panel.SetClass(player, id, "on", true);
        }
    }

    /// <summary>
    /// Two steps per card: when its time is up it gets <c>leaving</c> and slides out while still holding its place;
    /// <see cref="LeaveSeconds"/> later it is taken out of the stack and the rest move up.
    /// </summary>
    private static void Tick()
    {
        if (_panel is null || Stacks.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var (slot, stack) in Stacks.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid)
            {
                Stacks.Remove(slot);
                continue;
            }

            for (var i = 0; i < stack.Count; i++)
            {
                var card = stack[i];
                if (card.Leaving || card.Until > now) continue;
                card.Leaving = true;
                card.Until = now + LeaveSeconds;
                _panel.SetClass(player, $"t{i}", "leaving", true);
            }

            if (stack.RemoveAll(c => c.Leaving && c.Until <= now) == 0) continue;
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
