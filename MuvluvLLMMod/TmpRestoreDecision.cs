namespace MuvluvLLMMod;

/// <summary>
/// Applies the only restore operation permitted during a plugin-owned TMP refresh.
/// The decision is kept free of Unity types so its origin and validation rules can be tested
/// without loading the game.
/// </summary>
public static class TmpRestoreDecision
{
    public static string Resolve(
        TmpTranslationProvenance provenance,
        TmpTextAssignment assignment,
        string currentText,
        TmpTextAssignmentOrigin origin,
        Func<string, string?> sourceLookup)
    {
        if (origin != TmpTextAssignmentOrigin.PluginRefresh)
            return currentText;

        return provenance.TryRestore(
                assignment,
                currentText,
                origin,
                sourceLookup,
                out var source)
            ? source
            : currentText;
    }
}
