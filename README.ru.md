# CS2UIKit — интерфейс Panorama для плагинов CounterStrikeSharp

Готовые **тосты** и **голосования** в стиле CS2, свои кликабельные панели и штатная клавиша **B** — из C#, без
собственной разметки Panorama. Работает на сущности `custom_hud_layout`, которую Valve добавила 24 августа 2026 года.

![Тосты и окно голосования CS2UIKit](kit/workshop_preview.jpg)

```csharp
public override void Load(bool hotReload)   => UIKit.Init(this, hotReload);
public override void Unload(bool hotReload) => UIKit.Shutdown();

Toasts.Show(player, "Airdrop через 10 с", "Борт на подходе.", ToastStyle.Info);

Votes.Start(new VoteRequest { Question = "Следующая карта?", Options = new[] { "Dust II", "Inferno", "Mirage" } },
    result => { if (result.Winner >= 0) ChangeMap(result.Winner); });
```

> **2.0, предварительная версия.** Раньше репозиторий назывался *HudPanel* (1.x — помощник для своих панелей из двух
> файлов). 2.0 — библиотека с готовыми окнами; свои панели и клавиша B остались (`Panel`, `BuyMenuBridge`).

## Что внутри

* **Тосты** — тёмные карточки с размытием, цветной полосой и пиксельным узором; пять стилей, до четырёх на экране,
  новый выезжает снизу, остальные плавно поднимаются; строка сообщения, ссылка, звук. Курсор не забирают.
* **Голосования** — окно слева: вопрос, 2–5 вариантов со счётом и полосами, отсчёт, итог с победителем. Голос —
  `!1`…`!5` в чат, строка засчитывается и в чат не попадает. Звуки — штатные звуки голосования CS2.
* **Panel** — своя разметка: тексты, классы, захват курсора, скрытие частей штатного HUD, клики.
* **BuyMenuBridge** — открыть любую панель штатной клавишей B.

## Установка

1. **Сервер:** подключить `src/CS2UIKit/CS2UIKit.csproj` к плагину, `CS2UIKit.dll` положить рядом с dll плагина.
2. **Клиенты:** окна — аддон Workshop [CS2UIKit](https://steamcommunity.com/sharedfiles/filedetails/?id=3807024759)
   (`3807024759`), раздаётся MultiAddonManager через `mm_client_extra_addons`.
3. `UIKit.Init(this, hotReload)` в `Load`, `UIKit.Shutdown()` в `Unload`.

Попробовать — плагин `examples/Showcase`: `css_toast`, `css_uivote [3|4|5]`, `css_toastsound`.

## CS2 1.41.8.x

* **Тексты пустеют после захода игрока** (CounterStrikeSharp #1434) — через ~1,5 с после `EventPlayerConnectFull`
  вызывать `UIKit.Rebuild()`.
* **Клиенты не получают аддон** — MultiAddonManager 1.5.4 сломан патчем (#75), 1.6.1 требует Metamod новее, чем
  поддерживает CounterStrikeSharp 1.0.374. Собрать 1.5.4 со смещениями 376 и 616 из 1.6.1.

Подробности, API и ловушки — в [README на английском](README.md) и [docs/GOTCHAS.md](docs/GOTCHAS.md).

Лицензия — MIT.
