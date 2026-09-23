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

/// <summary>Where the toast stack sits on screen. In the bottom positions the stack grows upward.</summary>
public enum ToastPosition { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>
/// Toast notifications: a dark frosted card with a soft wash of the style colour, a round icon, a bold title, an
/// optional message and link. Up to four per player, oldest first: a new toast slides in after the others, and when
/// one leaves, the ones after it glide into its place. They never take the cursor, so a notice cannot interrupt
/// aiming, and play a short UI sound.
///
/// <code>
/// Toasts.Show(player, "Round started", "One of you is already infected.", ToastStyle.Warning);
/// Toasts.ShowAll("Airdrop incoming", "Crates land in 10 seconds.", ToastStyle.Info, seconds: 8);
/// </code>
///
/// How it moves: the layout has four card panels that all sit at the same spot. A card's place in the stack is only a
/// transform (<c>row-0</c>…<c>row-3</c>), so moving a card up is a plain transform tween — text never moves between
/// panels and nothing reflows (Panorama cannot animate a reflow). Needs the CS2UIKit Workshop addon on clients.
/// </summary>
public static class Toasts
{
    public const string Layout = "panorama/layout/custom_game/cs2uikit_toasts.xml";

    /// <summary>Cards visible at once per player. The layout has exactly this many card panels.</summary>
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

    /// <summary>
    /// A new card gets its text, style and row first and is switched on in a later network update: switched on in the
    /// same update it would skip the slide-in.
    /// </summary>
    private const float EnterDelay = 0.1f;

    private sealed class Card
    {
        public required int Panel;          // which of the four card panels (t0…t3) shows it
        public int Row;                     // place in the stack, 0 = first
        public required double Until;
        public bool Leaving;
    }

    private static Panel? _panel;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _timer;

    /// <summary>Cards per player in stack order (first = oldest). Leaving cards stay until they are gone.</summary>
    private static readonly Dictionary<int, List<Card>> Stacks = new();

    /// <summary>Row each card panel was last put in, per player: a freed panel keeps resting at its old row.</summary>
    private static readonly Dictionary<int, int[]> PanelRows = new();

    /// <summary>
    /// When a reused panel moves to another row, it glides there invisible first (the row tween is 0.32 s) and only
    /// then slides in — otherwise the slide-in would go diagonally.
    /// </summary>
    private const float MovedEnterDelay = 0.4f;

    public static void Show(CCSPlayerController player, string title, string? message = null,
        ToastStyle style = ToastStyle.Info, float? seconds = null, string? link = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var panel = EnsurePanel();

        if (!panel.IsOpen(player)) panel.Show(player);
        var bottom = Position is ToastPosition.BottomLeft or ToastPosition.BottomRight;
        panel.SetVariant(player, "toasts", "pos-", PositionClass(Position));
        panel.SetClass(player, "toasts", "stack-up", bottom);

        if (!Stacks.TryGetValue(player.Slot, out var stack)) Stacks[player.Slot] = stack = new List<Card>();
        PlaySound(player, style);

        var until = Server.CurrentTime + Math.Max(1f, seconds ?? DefaultSeconds);
        var free = FreePanel(stack);
        if (free < 0)
        {
            // All four panels are busy: the oldest card leaves now, and the new one comes in once its panel is free.
            var oldest = stack.FirstOrDefault(c => !c.Leaving);
            if (oldest is not null) StartLeaving(player, oldest);
            var slot = player.Slot;
            var left = (float)Math.Max(1.0, until - Server.CurrentTime);
            UIKit.Plugin.AddTimer((float)LeaveSeconds + 0.05f, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is null || !p.IsValid) return;
                Tick();   // collect the card that just left
                Add(p, title, message, style, left, link);
            });
            return;
        }

        Add(player, title, message, style, (float)(until - Server.CurrentTime), link);
    }

    private static void Add(CCSPlayerController player, string title, string? message, ToastStyle style, float seconds, string? link)
    {
        var panel = _panel!;
        if (!Stacks.TryGetValue(player.Slot, out var stack)) Stacks[player.Slot] = stack = new List<Card>();
        var free = FreePanel(stack);
        if (free < 0) return;

        var card = new Card { Panel = free, Row = stack.Count, Until = Server.CurrentTime + seconds };
        stack.Add(card);

        var id = $"t{free}";
        panel.SetText(player, id + "_title", title);
        panel.SetText(player, id + "_msg", message ?? string.Empty);
        panel.SetText(player, id + "_link", link ?? string.Empty);
        panel.SetClass(player, id, "has-msg", !string.IsNullOrEmpty(message));
        panel.SetClass(player, id, "has-link", !string.IsNullOrEmpty(link));
        panel.SetVariant(player, id, "style-", style.ToString().ToLowerInvariant());
        panel.SetClass(player, id, "leaving", false);
        panel.SetClass(player, id, "on", false);
        panel.SetVariant(player, id, "row-", card.Row.ToString());

        var rows = RowsOf(player.Slot);
        var delay = rows[free] == card.Row ? EnterDelay : MovedEnterDelay;
        rows[free] = card.Row;

        var owner = player.Slot;
        UIKit.Plugin.AddTimer(delay, () =>
        {
            var p = Utilities.GetPlayerFromSlot(owner);
            if (p is null || !p.IsValid || _panel is null || !Stacks.TryGetValue(owner, out var st) || !st.Contains(card)) return;
            _panel.SetClass(p, $"t{card.Panel}", "on", true);
        });
    }

    /// <summary>Show the same toast to every human on the server.</summary>
    public static void ShowAll(string title, string? message = null, ToastStyle style = ToastStyle.Info,
        float? seconds = null, string? link = null)
    {
        foreach (var player in Utilities.GetPlayers())
            Show(player, title, message, style, seconds, link);
    }

    /// <summary>Slide every toast of a player out at once.</summary>
    public static void Clear(CCSPlayerController player)
    {
        if (!Stacks.TryGetValue(player.Slot, out var stack)) return;
        foreach (var card in stack.Where(c => !c.Leaving).ToList()) StartLeaving(player, card);
    }

    private static Panel EnsurePanel()
    {
        if (_panel is not null) return _panel;
        _panel = new Panel(Layout, new PanelOptions { Root = "toasts", CaptureInput = false });
        _timer = UIKit.Plugin.AddTimer(0.1f, Tick, TimerFlags.REPEAT);
        return _panel;
    }

    private static int[] RowsOf(int slot)
    {
        if (!PanelRows.TryGetValue(slot, out var rows)) PanelRows[slot] = rows = new[] { 0, 0, 0, 0 };   // an unused panel rests where row 0 is
        return rows;
    }

    private static int FreePanel(List<Card> stack)
    {
        for (var i = 0; i < Slots; i++)
            if (stack.All(c => c.Panel != i)) return i;
        return -1;
    }

    private static void StartLeaving(CCSPlayerController player, Card card)
    {
        card.Leaving = true;
        card.Until = Server.CurrentTime + LeaveSeconds;
        _panel?.SetClass(player, $"t{card.Panel}", "leaving", true);
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
    /// Two steps per card. When its time is up it gets <c>leaving</c> and slides out of its row. Once the slide-out is
    /// over the card is dropped, its panel is freed, and every card after it moves up a row — a transform tween.
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

            foreach (var card in stack.Where(c => !c.Leaving && c.Until <= now).ToList())
                StartLeaving(player, card);

            if (stack.RemoveAll(c => c.Leaving && c.Until <= now) == 0) continue;
            for (var i = 0; i < Slots; i++)
            {
                if (stack.Any(c => c.Panel == i)) continue;
                // A freed panel: off, ready for the next card.
                _panel.SetClass(player, $"t{i}", "on", false);
                _panel.SetClass(player, $"t{i}", "leaving", false);
            }
            for (var row = 0; row < stack.Count; row++)
            {
                if (stack[row].Row == row) continue;
                stack[row].Row = row;
                RowsOf(slot)[stack[row].Panel] = row;
                _panel.SetVariant(player, $"t{stack[row].Panel}", "row-", row.ToString());
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

    internal static void Forget(int slot)
    {
        Stacks.Remove(slot);
        PanelRows.Remove(slot);
    }

    /// <summary>Map change: the entity is gone and so is every card on screen.</summary>
    internal static void Reset()
    {
        Stacks.Clear();
        PanelRows.Clear();
    }

    internal static void Stop()
    {
        _timer?.Kill();
        _timer = null;
        _panel = null;
        Stacks.Clear();
    }
}
