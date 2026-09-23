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
/// Toast notifications: a dark frosted card with a soft wash of the style colour, a round icon, a bold title, an
/// optional message and link. Up to four are stacked per player, oldest on top: a new toast slides in at the bottom
/// without disturbing the others, and when one leaves, the ones below glide up. They never take the cursor, so a
/// notice cannot interrupt aiming, and play a short UI sound.
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

    /// <summary>
    /// Wait before the very first card of a player. The stack becomes visible first; a card switched on in the same
    /// network update skips its transition and pops in, so it gets a clearly separate update.
    /// </summary>
    private const float FirstShowDelay = 0.35f;

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
        var card = new Card
        {
            Title = title,
            Message = message ?? string.Empty,
            Link = link ?? string.Empty,
            Style = style,
            Until = Server.CurrentTime + Math.Max(1f, seconds ?? DefaultSeconds),
        };

        PlaySound(player, style);

        if (!panel.IsOpen(player))
        {
            // First toast for this player: show the (empty) stack now and the card a beat later — see FirstShowDelay.
            // The stack then stays shown for good, so later toasts never hit this again.
            panel.Show(player);
            panel.SetVariant(player, "toasts", "pos-", PositionClass(Position));
            stack.Add(card);
            var slot = player.Slot;
            UIKit.Plugin.AddTimer(FirstShowDelay, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not null && p.IsValid && Stacks.TryGetValue(slot, out var st)) Render(p, st);
            });
            return;
        }

        panel.SetVariant(player, "toasts", "pos-", PositionClass(Position));
        if (stack.Count >= Slots)
        {
            // Full: the oldest (top) makes room at once and the rest glide up; the new card joins at the bottom a moment
            // later, so it slides in like any other instead of appearing in a slot that is already on.
            stack.RemoveAt(0);
            Render(player, stack, liftFrom: 0);
            var slot = player.Slot;
            UIKit.Plugin.AddTimer((float)LeaveSeconds, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is null || !p.IsValid || !Stacks.TryGetValue(slot, out var st) || st.Count >= Slots) return;
                st.Add(card);
                RenderSlot(p, st.Count - 1, card);
            });
            return;
        }
        stack.Add(card);
        RenderSlot(player, stack.Count - 1, card);
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
    /// Fill every slot from the stack (slot 0 = oldest, top). With <paramref name="liftFrom"/> the slots from that index
    /// on are re-filled one card higher than before, so they are pushed down by one card instantly (<c>.lift</c>) and
    /// released a beat later to glide up into place — the layout itself cannot animate a reflow.
    /// </summary>
    private static void Render(CCSPlayerController player, List<Card> stack, int liftFrom = -1)
    {
        var panel = _panel!;
        for (var i = 0; i < Slots; i++)
        {
            var id = $"t{i}";
            if (i >= stack.Count)
            {
                panel.SetClass(player, id, "on", false);
                panel.SetClass(player, id, "leaving", false);
                panel.SetClass(player, id, "lift", false);
                continue;
            }
            var lift = liftFrom >= 0 && i >= liftFrom;
            panel.SetClass(player, id, "lift", lift);
            RenderSlot(player, i, stack[i]);
        }

        if (liftFrom < 0) return;
        var slot = player.Slot;
        UIKit.Plugin.AddTimer(0.05f, () =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is null || !p.IsValid || _panel is null) return;
            for (var i = 0; i < Slots; i++) _panel.SetClass(p, $"t{i}", "lift", false);
        });
    }

    private static void RenderSlot(CCSPlayerController player, int index, Card card)
    {
        var panel = _panel!;
        var id = $"t{index}";
        panel.SetText(player, id + "_title", card.Title);
        panel.SetText(player, id + "_msg", card.Message);
        panel.SetText(player, id + "_link", card.Link);
        panel.SetClass(player, id, "has-msg", card.Message.Length > 0);
        panel.SetClass(player, id, "has-link", card.Link.Length > 0);
        panel.SetVariant(player, id, "style-", card.Style.ToString().ToLowerInvariant());
        panel.SetClass(player, id, "leaving", card.Leaving);
        panel.SetClass(player, id, "on", true);
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

            var first = stack.FindIndex(c => c.Leaving && c.Until <= now);
            if (first < 0) continue;
            stack.RemoveAll(c => c.Leaving && c.Until <= now);
            // Cards that were below the removed one move up a slot and glide into place. The stack itself stays
            // shown even when empty: hiding it would make the next first toast pop in again.
            Render(player, stack, liftFrom: first);
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
