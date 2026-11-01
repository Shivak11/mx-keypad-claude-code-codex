using Loupedeck.AgentKeypadPlugin;

var cases = new[]
{
    (true, "draft", "draft dictated", true),
    (true, "draft", "draft", false),
    (false, "draft", "draft dictated", false),
    (true, (string)null, "dictated", false),
    (true, "draft", (string)null, false),
    (true, "", "dictated", true),
};
foreach (var (restored, before, after, expected) in cases)
{
    if (CaptureOutcome.InsertionConfirmed(restored, before, after) != expected)
        throw new Exception("Unexpected insertion confirmation");
}
Console.WriteLine($"Passed {cases.Length} capture outcome cases.");
