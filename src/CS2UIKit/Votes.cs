using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2UIKit;

/// <summary>A vote to run: the question, its options and how long it lasts.</summary>
public sealed class VoteRequest
{
    /// <summary>The question. <see cref="QuestionFor"/> overrides it per player (translations).</summary>
    public string Question { get; init; } = "";

    /// <summary>Per-player question, e.g. by the client language. Null — <see cref="Question"/> for everyone.</summary>
    public Func<CCSPlayerController, string>? QuestionFor { get; init; }

    /// <summary>
    /// Two to five options, answered with !1…!5 in chat. Null — a yes/no vote with <see cref="VoteTexts.Yes"/>
    /// and <see cref="VoteTexts.No"/>: the first option is "yes", the second "no".
    /// </summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Per-player options (translations). Must return as many options as <see cref="Options"/>.</summary>
    public Func<CCSPlayerController, IReadOnlyList<string>>? OptionsFor { get; init; }

    /// <summary>How long the vote lasts, seconds. It ends earlier once every voter has voted.</summary>
    public float Seconds { get; init; } = 20f;

    /// <summary>
    /// Who may vote. Null — every human on the server when the vote starts. People who join later see nothing:
    /// the number of voters is fixed at the start, so the result does not move under the players' feet.
    /// </summary>
    public Func<CCSPlayerController, bool>? Voters { get; init; }

    /// <summary>
    /// For yes/no votes: share of the votes cast that "yes" needs, 0…1. Default 0.5 — more yes than no (a tie
    /// fails). Ignored by multi-option votes, where the option with most votes wins (a tie — no winner).
    /// </summary>
    public double PassShare { get; init; } = 0.5;

    /// <summary>Fewest votes cast for the result to count. Below it the vote fails with no winner.</summary>
    public int MinVotes { get; init; } = 1;

    internal bool YesNo => Options is null && OptionsFor is null;
}

/// <summary>How a vote ended.</summary>
/// <param name="Winner">Index of the winning option; −1 when nobody won (tie, too few votes, a failed yes/no).</param>
/// <param name="Counts">Votes per option.</param>
/// <param name="Voters">How many could vote.</param>
/// <param name="Cancelled">Stopped with <see cref="Votes.Cancel"/>.</param>
public sealed record VoteResult(int Winner, int[] Counts, int Voters, bool Cancelled)
{
    /// <summary>For yes/no votes: "yes" won.</summary>
    public bool Passed => Winner == 0;

    public int Cast => Counts.Sum();
}

/// <summary>Why <see cref="Votes.Start"/> did not start a vote.</summary>
public enum VoteDenied
{
    /// <summary>Started.</summary>
    None,
    /// <summary>Another vote is running: one card, one vote at a time.</summary>
    Busy,
    /// <summary>The previous one ended moments ago (<see cref="Votes.CooldownSeconds"/>).</summary>
    Cooldown,
    /// <summary>Nobody may vote: no humans, or the filter let nobody through.</summary>
    Empty,
    /// <summary>Fewer than two or more than five options.</summary>
    BadOptions,
}

/// <summary>The card's fixed words, per player. Everything is English by default; give your own for translations.</summary>
public sealed record VoteTexts(
    string Header = "Vote",
    string Result = "Result",
    string Yes = "Yes",
    string No = "No",
    string Seconds = "{0} s",
    string Hint = "Vote in chat: !1–!{0}",
    string Passed = "Passed",
    string Failed = "Failed",
    string NoWinner = "No decision");

/// <summary>
/// Votes on a card of their own: a question, two to five options with live counts and bars, a countdown, and the
/// result at the end. Players answer with <c>!1…!5</c> in chat (<c>/1</c> votes without a chat line). Nothing takes the
/// cursor, so voting never interrupts aiming.
///
/// Why not F1/F2 like the stock vote: CS2 sends <c>vote option1…5</c> only while its own vote panel is up, and that
/// panel cannot be hidden — it draws under the card. Without it the keys send nothing (checked 2026-09-24: neither
/// the networked <c>vote_controller</c> state nor digits help; digits and F switch weapons client-side). A player who
/// wants keys can bind them: <c>bind f1 css_1</c>.
///
/// <code>
/// Votes.Start(new VoteRequest { Question = "Change the map to de_inferno?" }, result =>
/// {
///     if (result.Passed) Server.ExecuteCommand("changelevel de_inferno");
/// });
///
/// Votes.Start(new VoteRequest { Question = "Next map?", Options = new[] { "Dust II", "Inferno", "Mirage" } },
///     result => { if (result.Winner >= 0) ChangeMap(result.Winner); });
/// </code>
///
/// Sounds are the stock CS2 vote sounds (<c>Vote.Created</c>, <c>Vote.Cast.Yes</c>/<c>No</c>,
/// <c>Vote.Passed</c>/<c>Failed</c>), so a vote sounds like a vote. Needs the CS2UIKit Workshop addon on clients.
/// </summary>
public static class Votes
{
    public const string Layout = "panorama/layout/custom_game/cs2uikit_votes.xml";
    private const int MaxOptions = 5;

    /// <summary>Pause after a vote before the next may start, seconds.</summary>
    public static float CooldownSeconds { get; set; } = 5f;

    /// <summary>How long the result stays on the card, seconds.</summary>
    public static float ResultSeconds { get; set; } = 4f;

    /// <summary>Card words per player. Default: English.</summary>
    public static Func<CCSPlayerController, VoteTexts> Texts { get; set; } = _ => new VoteTexts();

    /// <summary>Sounds; null or empty for silence.</summary>
    public static string? SoundStart { get; set; } = "Vote.Created";
    public static string? SoundYes { get; set; } = "Vote.Cast.Yes";
    public static string? SoundNo { get; set; } = "Vote.Cast.No";
    public static string? SoundPassed { get; set; } = "Vote.Passed";
    public static string? SoundFailed { get; set; } = "Vote.Failed";

    /// <summary>A vote is running (the result on screen does not count).</summary>
    public static bool Active => _request is not null && !_ended;

    /// <summary>Someone voted: the player and the option index.</summary>
    public static event Action<CCSPlayerController, int>? Cast;

    private static Panel? _panel;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _tick;
    private static bool _commands;
    private static readonly List<(string Name, CommandInfo.CommandCallback Handler)> ChatCommands = new();

    private static VoteRequest? _request;
    private static Action<VoteResult>? _done;
    private static int _optionCount;
    private static bool _ended;
    private static float _startedAt;
    private static float _endsAt;
    private static float _freeAt;
    private static float _hideAt;
    private static readonly HashSet<int> Eligible = new();
    private static readonly HashSet<int> Shown = new();
    private static readonly Dictionary<int, int> Choice = new();

    /// <summary>Start a vote. Returns why it did not start, or <see cref="VoteDenied.None"/>.</summary>
    public static VoteDenied Start(VoteRequest request, Action<VoteResult>? done = null)
    {
        var count = request.YesNo ? 2 : (request.Options?.Count ?? 0);
        if (count < 2 || count > MaxOptions) return VoteDenied.BadOptions;
        if (Active) return VoteDenied.Busy;
        var now = Server.CurrentTime;
        if (now < _freeAt) return VoteDenied.Cooldown;

        var voters = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .Where(p => request.Voters?.Invoke(p) ?? true)
            .ToList();
        if (voters.Count == 0) return VoteDenied.Empty;

        var panel = EnsurePanel();
        ClearCard();

        _request = request;
        _done = done;
        _optionCount = count;
        _ended = false;
        _startedAt = now;
        _endsAt = now + Math.Max(5f, request.Seconds);
        _hideAt = 0;
        Choice.Clear();
        Eligible.Clear();
        foreach (var p in voters) Eligible.Add(p.Slot);

        foreach (var player in voters)
        {
            var texts = Texts(player);
            var options = OptionsOf(player, texts);
            if (!panel.IsOpen(player)) panel.Show(player);
            Shown.Add(player.Slot);

            panel.SetText(player, "vote_header", texts.Header);
            panel.SetText(player, "vote_question", request.QuestionFor?.Invoke(player) ?? request.Question);
            panel.SetText(player, "vote_hint", string.Format(texts.Hint, count));
            panel.SetClass(player, "vote_card", "yesno", request.YesNo);
            panel.SetClass(player, "vote_card", "ended", false);
            panel.SetVariant(player, "vote_card", "result-", null);
            for (var i = 0; i < MaxOptions; i++)
            {
                var used = i < count;
                panel.SetClass(player, $"vote_opt_{i}", "used", used);
                panel.SetClass(player, $"vote_opt_{i}", "mine", false);
                panel.SetClass(player, $"vote_opt_{i}", "winner", false);
                if (!used) continue;
                panel.SetText(player, $"vote_key_{i}", $"!{i + 1}");
                panel.SetText(player, $"vote_label_{i}", i < options.Count ? options[i] : $"#{i + 1}");
            }
            PlaySound(player, SoundStart);
        }
        Refresh();

        // Switched on in a later update: turned on together with the rest the card would pop in instead of sliding.
        UIKit.Plugin.AddTimer(0.1f, () =>
        {
            if (_panel is null || _request != request) return;
            foreach (var slot in Shown)
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not null && p.IsValid) _panel.SetClass(p, "vote_card", "on", true);
            }
        });
        return VoteDenied.None;
    }

    /// <summary>Vote for a player, as if they pressed F(index + 1). False when they may not vote now.</summary>
    public static bool Vote(CCSPlayerController player, int index)
    {
        if (!Active || _panel is null || !player.IsValid || !Eligible.Contains(player.Slot)) return false;
        if (index < 0 || index >= _optionCount) return false;
        if (Choice.TryGetValue(player.Slot, out var was) && was == index) return true;

        if (Choice.TryGetValue(player.Slot, out var old)) _panel.SetClass(player, $"vote_opt_{old}", "mine", false);
        Choice[player.Slot] = index;
        _panel.SetClass(player, $"vote_opt_{index}", "mine", true);
        PlaySound(player, _request!.YesNo && index == 1 ? SoundNo : SoundYes);
        Cast?.Invoke(player, index);
        Refresh();
        if (Choice.Count >= Eligible.Count) Finish(false);
        return true;
    }

    /// <summary>Stop the running vote without a result. The callback gets <c>Cancelled = true</c>.</summary>
    public static void Cancel()
    {
        if (Active) Finish(true);
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────

    private static Panel EnsurePanel()
    {
        if (_panel is not null) return _panel;
        _panel = new Panel(Layout, new PanelOptions { Root = "votes", CaptureInput = false });
        _tick = UIKit.Plugin.AddTimer(0.25f, Tick, TimerFlags.REPEAT);
        if (!_commands)
        {
            // !1…!5 in chat arrive through CounterStrikeSharp's chat triggers as css_1 … css_5; a bound key works too.
            for (var i = 1; i <= MaxOptions; i++)
            {
                var index = i - 1;
                CommandInfo.CommandCallback handler = (player, _) => { if (player is not null) Vote(player, index); };
                UIKit.Plugin.AddCommand($"css_{i}", $"CS2UIKit: vote for option {i}", handler);
                ChatCommands.Add(($"css_{i}", handler));
            }
            _commands = true;
        }
        return _panel;
    }

    private static IReadOnlyList<string> OptionsOf(CCSPlayerController player, VoteTexts texts)
    {
        var request = _request!;
        if (request.YesNo) return new[] { texts.Yes, texts.No };
        return request.OptionsFor?.Invoke(player) ?? request.Options!;
    }

    private static int[] Counts()
    {
        var counts = new int[_optionCount];
        foreach (var index in Choice.Values) counts[index]++;
        return counts;
    }

    /// <summary>Counts, bars and the countdown on every card.</summary>
    private static void Refresh()
    {
        if (_panel is null || _request is null) return;
        var counts = Counts();
        var total = Math.Max(1, counts.Sum());
        var now = Server.CurrentTime;
        var left = Math.Max(0f, _endsAt - now);
        var length = Math.Max(1f, _endsAt - _startedAt);
        var timerStep = (int)Math.Ceiling(left / length * 20);

        foreach (var slot in Shown)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid) continue;
            var texts = Texts(player);
            _panel.SetText(player, "vote_time", _ended ? string.Empty : string.Format(texts.Seconds, (int)Math.Ceiling(left)));
            _panel.SetVariant(player, "vote_timer_fill", "t", timerStep.ToString());
            for (var i = 0; i < _optionCount; i++)
            {
                _panel.SetText(player, $"vote_count_{i}", counts[i].ToString());
                _panel.SetVariant(player, $"vote_fill_{i}", "w", ((int)Math.Round(counts[i] * 10.0 / total)).ToString());
            }
        }
    }

    private static float _lastSecond = -1;

    private static void Tick()
    {
        if (_request is null) return;
        var now = Server.CurrentTime;
        if (!_ended)
        {
            if (now >= _endsAt) { Finish(false); return; }
            // The countdown moves once a second, not four times: fewer messages to every player.
            var second = (float)Math.Ceiling(_endsAt - now);
            if (second != _lastSecond) { _lastSecond = second; Refresh(); }
            return;
        }
        if (_hideAt > 0 && now >= _hideAt) HideCard();
    }

    private static void Finish(bool cancelled)
    {
        var request = _request!;
        _ended = true;
        var now = Server.CurrentTime;
        _freeAt = now + CooldownSeconds;
        _hideAt = now + ResultSeconds;

        var counts = Counts();
        var cast = counts.Sum();
        var winner = -1;
        if (!cancelled && cast >= Math.Max(1, request.MinVotes))
        {
            if (request.YesNo)
                winner = counts[0] > cast * request.PassShare ? 0 : 1;
            else
            {
                var best = counts.Max();
                if (counts.Count(c => c == best) == 1) winner = Array.IndexOf(counts, best);
            }
        }
        var result = new VoteResult(winner, counts, Eligible.Count, cancelled);
        var passed = request.YesNo ? result.Passed : winner >= 0;

        Refresh();
        if (_panel is not null)
        {
            foreach (var slot in Shown)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is null || !player.IsValid) continue;
                var texts = Texts(player);
                _panel.SetClass(player, "vote_card", "ended", true);
                _panel.SetVariant(player, "vote_card", "result-", winner < 0 && !request.YesNo ? "none" : passed ? "pass" : "fail");
                var verdict = winner < 0 && !request.YesNo ? texts.NoWinner : passed ? texts.Passed : texts.Failed;
                _panel.SetText(player, "vote_header", $"{texts.Result}: {verdict}");
                if (winner >= 0) _panel.SetClass(player, $"vote_opt_{winner}", "winner", true);
                PlaySound(player, passed ? SoundPassed : SoundFailed);
            }
        }

        var done = _done;
        _done = null;
        try { done?.Invoke(result); }
        catch (Exception e) { UIKit.Log($"vote callback failed — {e.Message}"); }
    }

    private static void HideCard()
    {
        _hideAt = 0;
        if (_panel is not null)
        {
            foreach (var slot in Shown)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is not null && player.IsValid) _panel.SetClass(player, "vote_card", "on", false);
            }
        }
        _request = null;
    }

    /// <summary>A new vote starts from a clean card: last vote's viewers who are not voters this time lose it.</summary>
    private static void ClearCard()
    {
        if (_panel is null) return;
        foreach (var slot in Shown)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not null && player.IsValid) _panel.SetClass(player, "vote_card", "on", false);
        }
        Shown.Clear();
    }

    private static void PlaySound(CCSPlayerController player, string? sound)
    {
        if (string.IsNullOrWhiteSpace(sound)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;
        try { pawn.EmitSound(sound, new RecipientFilter(player), 1f); }
        catch { /* a missing sound is never worth a broken vote */ }
    }

    // ── called by UIKit ──────────────────────────────────────────────────────────────────────────

    internal static void Forget(int slot)
    {
        Shown.Remove(slot);
        Choice.Remove(slot);
        if (Eligible.Remove(slot) && Active && Choice.Count >= Eligible.Count && Eligible.Count > 0) Finish(false);
    }

    /// <summary>Map change: the entity and every card are gone; a running vote ends without a result.</summary>
    internal static void Reset()
    {
        if (Active) Finish(true);
        _request = null;
        _hideAt = 0;
        Shown.Clear();
        Choice.Clear();
        Eligible.Clear();
    }

    internal static void Stop()
    {
        if (Active) Finish(true);
        _tick?.Kill();
        _tick = null;
        if (_commands && UIKit.Initialized)
        {
            foreach (var (name, handler) in ChatCommands) UIKit.Plugin.RemoveCommand(name, handler);
            ChatCommands.Clear();
            _commands = false;
        }
        _panel = null;
        _request = null;
        Shown.Clear();
        Choice.Clear();
        Eligible.Clear();
    }
}
