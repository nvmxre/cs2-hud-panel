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

**Cause.** An attribute value CS2 does not know. The compiler accepts the file; the client refuses it
at runtime and stays silent.

The one that caught us was image scaling. `scaling="stretch-to-fit-preserve-aspect"` exists in other
Source 2 games — in CS2 the engine only knows `stretch`, and the layout died.

**Fix.** Do not use `<Image scaling=…>`. Put pictures in as panel backgrounds instead:

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

## A player connects and inherits somebody else's open panel

**Cause.** Per-player state is keyed by **slot**, and slots are reused. Whatever the previous occupant
left behind is what the new player gets.

**Fix.** Explicitly hide the panel when a player spawns. Do it again a couple of seconds later: right
after spawn the client may not have applied the layout yet and will overwrite your state with its own.

---

## A server cannot rebind a player's keys

```
[InputService] Cannot execute concommand 'bind', missing required FCVAR flag
```

`ExecuteClientCommand("bind 1 css_something")` is rejected. Commands carry flags describing who may run
them, and `bind` is not one a server may run on a client — deliberately, so that joining a server
cannot rewrite your settings.

Intercepting the commands those keys send does not work either: `slot1…slot0` and `buymenu` do not
arrive as console commands on the server, so `AddCommandListener` never fires for them.

**What this means.** Number-key menus require the player's cooperation. Ship them a line to paste:

```
bind b "buymenu; pz_buymenu"; bind 1 "slot1; pz_buy1"
```

Put the **stock command first**. Then the key keeps working everywhere else: on other servers your
commands are unknown and ignored, while `buymenu` and `slot1` behave as usual.

The useful corollary: the old fear that a plugin can leave a player without weapon switching is
unfounded. A server cannot touch binds at all.

---

## The stock buy menu opens on top of yours and eats the input

**Cause.** `B` is bound to `buymenu`, you cannot take that key, and while the stock menu is open it
consumes number keys and clicks at the UI level — before any binding is consulted.

Writing `false` into `CCSPlayerPawn.IsBuyMenuOpen` does not help. That field *reports* state to the
server, it does not control the client.

**Fix.** Leave the key alone and take away its meaning: `mp_buy_anywhere 0`, `mp_buytime 0`, and remove
the `func_buyzone` entities on round start. `B` then opens nothing and stops competing.

Worth knowing: the money indicator in the bottom-left survives this. It does not depend on whether
buying is possible.

---

## Changes to the layout do not show up

Panorama caches layouts for the whole session. Reconnecting to the server is not enough — restart the
game client.
