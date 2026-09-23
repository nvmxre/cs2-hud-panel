using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CS2UIKit;
using Microsoft.Extensions.Logging;

namespace CS2UIKit.Showcase;

/// <summary>Every ready-made CS2UIKit window, one command each. Needs the CS2UIKit addon on the client.</summary>
public sealed class ShowcasePlugin : BasePlugin
{
    public override string ModuleName => "CS2UIKit Showcase";
    public override string ModuleVersion => "2.0.0";

    public override void Load(bool hotReload) => UIKit.Init(this, hotReload, m => Logger.LogInformation("{Message}", m));

    public override void Unload(bool hotReload) => UIKit.Shutdown();

    /// <summary>css_toastsound [event|off] — try another sound for toasts, e.g. UIPanorama.submenu_leveloptions_slidein.</summary>
    [ConsoleCommand("css_toastsound", "Toast sound: css_toastsound <sound event>|off")]
    public void OnToastSound(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1) : "";
        Toasts.Sound = arg is "" or "off" ? null : arg;
        command.ReplyToCommand($"Toast sound: {Toasts.Sound ?? "off"}");
        if (player is not null && player.IsValid)
            Toasts.Show(player, "Sound check", Toasts.Sound ?? "silent", ToastStyle.Neutral);
    }

    /// <summary>css_toast [info|success|warning|danger|neutral|all] — one toast, or one of each.</summary>
    [ConsoleCommand("css_toast", "Show a toast: css_toast [info|success|warning|danger|neutral|all]")]
    public void OnToast(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid) return;
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "warning";

        if (arg == "all")
        {
            Toasts.Show(player, "Server restart", "The map changes in 2 minutes.", ToastStyle.Neutral);
            Toasts.Show(player, "Airdrop incoming", "Crates land in 10 seconds.", ToastStyle.Info);
            Toasts.Show(player, "Bleeding stopped", "The medkit did its job.", ToastStyle.Success);
            Toasts.Show(player, "Global Cooldown 20 Hours", "VAC has flagged your gameplay as irregular",
                ToastStyle.Warning, link: "https://blog.counter-strike.net/index.php/faq");
            return;
        }

        var style = arg switch
        {
            "info" => ToastStyle.Info,
            "success" => ToastStyle.Success,
            "danger" => ToastStyle.Danger,
            "neutral" => ToastStyle.Neutral,
            _ => ToastStyle.Warning,
        };
        Toasts.Show(player, $"{style} toast", "A message line under the title.", style,
            link: style == ToastStyle.Warning ? "https://github.com/nvmxre/cs2-ui-kit" : null);
    }
}
