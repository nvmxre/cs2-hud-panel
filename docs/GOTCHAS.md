# Gotchas

Everything below cost us real time. Each entry gives the symptom first, because that is what you will
be searching for.

---

## The panel spawns, the server is happy, the screen stays empty

**Cause.** The layout name must be the full path to the **source** file, extension included:

```csharp
new HudPanel("panorama/layout/custom_game/my_menu.xml")   // correct
new HudPanel("my_menu")                                   // silently renders nothing
new HudPanel("panorama/layout/custom_game/my_menu.vxml_c") // also wrong — not the compiled name
```

With a wrong value everything looks fine server-side: the entity spawns, your open call runs, input
capture reports `true`. The client just never finds the layout and says nothing about it.

---

## `RESOURCE COMPILE ERROR: Found root panel with 'id' attribute, which is not permitted`

**Cause.** Exactly what it says — the outermost `<Panel>` cannot have an `id`.

**Fix.** Put the id on a panel *inside* the root and toggle that one. This also solves a second
problem: the root is always visible, so anything you style on it (a dimmed backdrop, for instance)
can never be switched off.

---

## The whole layout stops loading after an edit, with no error

**Cause.** Something in the file CS2 does not accept. The compiler takes it; the client refuses it at
runtime and stays silent — the server meanwhile reports the panel as shown.

Two we hit:

* `scaling="stretch-to-fit-preserve-aspect"` on `<Image>`. The value exists in other Source 2 games;
  in CS2 the engine only knows `stretch`, and the layout died.
* `<Image src="s2r://…/icon.vsvg">` — a stock vector icon in an `Image` tag. Same result: the menu
  never opens.

**Fix.** Do not use `<Image>` for pictures at all. Put them in as panel backgrounds:

```css
.tile-image
{
	background-image: url("s2r://panorama/images/custom_game/pz/ak-47.vtex");
	background-size: contain;
	background-position: 50% 50%;
	background-repeat: no-repeat;
}
```

`background-size: contain` keeps the aspect ratio, which is what the `scaling` attribute was for.

Two things worth knowing while you are here. The game's own images work as backgrounds and do not have
to be in your addon: weapon renders `s2r://panorama/images/econ/weapons/base_weapons/weapon_ak47_png.vtex`,
agent portraits `econ/characters/customplayer_<model>_png.vtex`, textures such as
`backgrounds/linemap_png.vtex` — a whole server menu can be built out of them, and
`world-blur: gaussian( 2, 2, 2 )` on a panel blurs the 3D world behind it. And if you compile your own
`.vtex`, use BGRA8888: DXT5 comes out as a pink picture on blue.

---

## `[custom_hud] Layout contains disallowed panel type 'MapPlayerPreviewPanel'`

**Cause.** The host checks the layout against a whitelist of panel types: `Panel`, `Label`, `Image`,
`Button`. Anything else — the 3D character preview the stock buy menu uses, scroll bars, text entries —
is rejected with the message above, and the layout does not load.

**What this means.** Only clicks travel to the server (`OnCustomHudClicked`). There is no hover event,
no script, no scene: "hover a weapon and the character picks it up" cannot be built. `:hover` in the
stylesheet is the whole of your hover budget, and it is client-side.

---

## A cursor nobody can release; panels that open by themselves

**Cause.** The entity outlives your plugin. If `Unload` does not remove it, the next load creates a
second one beside the first. After a dozen reloads there are a dozen panels, each with its own input
capture and its own per-player visibility — and your calls only ever reach the newest.

The symptoms are genuinely confusing: a command toggles one element and not another, `false` shows in
your logs while the panel is plainly on screen, nothing you do releases the mouse.

**Fix.** Two things, both in this library:

```csharp
public override void Unload(bool hotReload) => _panel?.Stop(this);
```

and, on spawn, remove every `custom_hud_layout` already in the world. That second one matters after a
server crash, when nothing got the chance to clean up.

---

## Weapon draw animations crawl after a few hot reloads

**Symptom.** After a day of `css_plugins reload` the game starts to stutter — but only the client side
of it: weapon draw animations, some effects. Server metrics are spotless: tick rate steady, CPU idle,
memory free, no errors in the log, one copy of the plugin loaded.

**Cause.** A reload removes your handlers and timers. It does not remove the entities you created. The
panel is handled (see above), but anything else — lights, props, particles, especially things parented
to a weapon — stays behind, and the plugin's own bookkeeping of them is gone, so its cleanup never finds
them. They accumulate reload after reload.

**Fix.** Restart the server process, not the plugin, whenever a change touches entities or hooks. In a
deploy script: nobody online → `systemctl restart`, people online → hot reload now and a restart later.
And write cleanup that searches the world by name rather than trusting a dictionary a reload empties.

---

## A player connects and inherits somebody else's open panel

**Cause.** Per-player state is keyed by **slot**, and slots are reused. Whatever the previous occupant
left behind is what the new player gets.

**Fix.** Explicitly hide the panel when a player spawns. Do it again a couple of seconds later: right
after spawn the client may not have applied the layout yet and will overwrite your state with its own.

---

## `Entity system yet is not initialized` — and it never recovers

**Symptom.** The server is healthy: map loaded, players running around, `status` clean. The plugin does
nothing, and every entity call logs the line above. `css_plugins reload` does not help.

**Cause.** CounterStrikeSharp keeps the entity-list pointer in a static `Lazy<IntPtr>`, and `Lazy` in
its default mode caches a failed factory forever. One call before the first map has loaded — from
`Load`, from `OnMapStart` (which fires at the *beginning* of the load, before entities exist), from a
timer started in `Load` — and the process is blind until it is restarted. The field lives in
CounterStrikeSharp's own assembly, so reloading your plugin cannot reset it.

**Fix.** Never touch entities before the first `round_start`. This library spawns on round start and
takes the `hotReload` flag in `Start` for the one case where a map is known to be running. To find the
culprit in your own code: start the server with `-condebug` and look for the **first** occurrence of the
line in `console.log`; everything after it is the cache echoing.

---

## A server cannot rebind a player's keys

```
[InputService] Cannot execute concommand 'bind', missing required FCVAR flag
```

`ExecuteClientCommand("bind 1 css_something")` is rejected. Commands carry flags describing who may run
them, and `bind` is not one a server may run on a client — deliberately, so that joining a server
cannot rewrite your settings.

Intercepting the commands those keys send mostly does not work either: `slot1…slot0` never arrive as
console commands on the server, so `AddCommandListener` never fires for them. The buy key is the
exception, and it is a big one — see the next entry.

**What this means.** Number-key menus require the player's cooperation. Ship them a line to paste:

```
bind b "buymenu; pz_buymenu"; bind 1 "slot1; pz_buy1"
```

Put the **stock command first**. Then the key keeps working everywhere else: on other servers your
commands are unknown and ignored, while `buymenu` and `slot1` behave as usual.

The useful corollary: the old fear that a plugin can leave a player without weapon switching is
unfounded. A server cannot touch binds at all.

---

## Opening your panel with the B key

**An earlier version of this page said the opposite** — disable buying so that `B` "stops competing".
That advice was built on one wrong observation and cost us four days. It is withdrawn: the stock buy
menu is the way in, not the enemy.

The server cannot see the key. But while the stock buy menu is open, **the client itself** puts the
class `HUD_BUYMENU_VISIBLE` on the HUD root, and your layout is inside that root. So the plugin never
opens anything. It marks the players who are *allowed* the panel with a class on the window, and one
rule shows the window when both classes are present:

```css
.HUD_BUYMENU_VISIBLE .my-window.native
{
	visibility: visible;
	opacity: 1;
	position: 0px 0px 0px;
}
```

Three conditions, all handled by `BuyMenuBridge`:

1. **Buying stays enabled.** `mp_buy_anywhere 1`, `mp_buytime 60000`. With buying disabled the client
   opens nothing and sends nothing — that is the observation the old advice was built on. The game-mode
   config resets both cvars every round, so they are re-applied on `round_start`.
2. **Stock purchases are blocked at the command level.** `buy`, `autobuy` and `rebuy` are answered with
   `HookResult.Handled`. Otherwise the player buys through the stock menu that is open under yours,
   past your prices and unlocks — we shipped that bug once.
3. **The cursor is captured while the stock menu is open.** Hover works without capture (it is
   client-side CSS); clicks do not — the click sound plays and nothing arrives. The client announces the
   menu with the commands `open_buymenu` and `close_buymenu`, which do reach `AddCommandListener` as
   long as buying is enabled. Capture on the first, release on the second.

Two consequences for your code. **Draw before the key is pressed**: the client shows the panel without
asking the server, so an empty window is what the player gets otherwise — draw when you grant the key
and again on `open_buymenu`. And **the server cannot close the panel** while the class holds it; a Close
button sends `close_buymenu` to the client instead (`player.ExecuteClientCommand`) and the panel goes
with the stock menu.

A player who is *not* allowed the panel still gets the stock menu on B, with purchases blocked.
`mp_buytime 0` closes it for everybody at once — the way to end a shopping phase.

This is what the buy menu in PROJECT ZERO runs on.

---

## Clicks between your tiles land on the stock buy menu

**Symptom.** "I clicked between the AWP and the Molotov and got an M4A1-S." Labels of the stock menu
show through the gaps.

**Cause.** The stock buy menu is open underneath your panel, with its own tiles. Wherever your panel
does not catch the mouse, the click falls through to them.

**Fix.** A full-screen backdrop with `hittest="true"` and a near-opaque background (`opacity: 0.985`
on black, or a `#rrggbbfb` colour). Events stop at the backdrop and nothing shows through. The example
layout has one.

---

## `IsBuyMenuOpen` flickers

`CCSPlayerPawn.IsBuyMenuOpen` is tempting as the source of truth for capture. It is not: on consecutive
reads it alternates between true and false while the menu is plainly open, and capture switched by it
goes off in the middle of a click. Reading it can also throw during map transitions.

Use the commands (`open_buymenu` / `close_buymenu`) as the source of truth. If you poll the flag at
all, use it only to switch capture **on** — never off. `BuyMenuBridge.Poll` does exactly that.

---

## The B panel opens empty

The client opened it, not you. See "Draw before the key is pressed" above: fill the panel when you
grant the key (`BuyMenuBridge.Prepare` fires there) and on every `open_buymenu` (it fires there too).

---

## `@keyframes` never play; the transition does

In `custom_hud_layout`, `@keyframes` that animate `transform` do not run at all — the panel just sits
at its final state. Several evenings of "the effect does not change when I set the class" were exactly
this.

What works: `transition-property: opacity, transform` triggered by a class the plugin toggles with
`SetHasClassForPlayer`. Keyframes on `opacity` alone do run (an endless pulse is fine). `width`, `height`
and `visibility` cannot be animated at all.

Two more rules from the same evenings. An animation and a transition on the same property of the same
panel do not coexist — put the transition on a wrapper panel. And an `#id` selector beats a `.class`
selector; with equal specificity the rule lower in the file wins.

---

## `PrintToCenterHtml` flashes once a second

Not a `custom_hud_layout` problem, but it is why most people come looking for one, so it belongs here.

An HTML centre message re-runs its entry animation about once a second. Nothing you do to the message
stops it — the re-render is driven by the round timer, not by your call.

**The workaround.** Poggu's trick, packaged by M-archand as
[CS2FlashingHtmlHudFix](https://github.com/M-archand/CS2FlashingHtmlHudFix): keep `GameRestart` set while
`RestartRoundTime < Server.CurrentTime`. The panel then holds still.

Worth knowing, and enough if a static block of HTML in the centre of the screen is all you need. It does
not make the thing clickable, and it does not give you a layout you control — that is what this library
is for.

---

## Changes to the layout do not show up

Panorama caches layouts for the whole session. Reconnecting to the server is not enough — restart the
game client.

On a live server there is a second reason: players get the layout from your Workshop addon, and a plugin
deploy does not touch it. Until you republish the addon, everybody keeps the old menu — with the new
server logic underneath it.
