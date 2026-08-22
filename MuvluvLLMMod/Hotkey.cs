using UnityEngine;
using UnityEngine.InputSystem;

namespace MuvluvLLMMod;

/// <summary>
/// Handles the display toggle and the periodic TMP refresh on Unity's main thread.
/// </summary>
public sealed class Hotkey : MonoBehaviour
{
    private float refreshElapsed;

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[Key.F2].wasPressedThisFrame)
        {
            Config.Translation.Value = !Config.Translation.Value;
            var progress = Plugin.ProgressSnapshot;
            Logger.Info(
                $"[LLM] display={(Config.Translation.Value ? "on" : "off")}; "
                + $"progress completed={progress.Completed}, in-flight={progress.InFlight}, failed={progress.Failed}");
        }

        refreshElapsed += Time.deltaTime;
        if (refreshElapsed < Math.Max(0.01f, Config.LlmRefreshPeriodSeconds.Value))
            return;

        refreshElapsed = 0f;
        Patch.RefreshAllTmpText();
    }
}
