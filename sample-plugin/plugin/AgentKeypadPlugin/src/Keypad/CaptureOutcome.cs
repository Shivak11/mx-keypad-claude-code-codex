namespace Loupedeck.AgentKeypadPlugin
{
    // A clipboard change alone does not prove that text landed in the intended field.
    internal static class CaptureOutcome
    {
        public static bool InsertionConfirmed(bool restored, string before, string after) =>
            restored && before != null && after != null && before != after;
    }
}
