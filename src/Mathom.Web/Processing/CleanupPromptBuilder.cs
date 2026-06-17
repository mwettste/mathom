namespace Mathom.Web.Processing;

public static class CleanupPromptBuilder
{
    public static string BuildSystemPrompt() =>
        """
        You clean up and classify a user's quickly-captured note.
        Respond ONLY with a JSON object, no prose, with exactly these fields:
        {
          "title": string (max ~8 words),
          "clean_text": string (the note, cleaned up, fixing transcription errors, preserving meaning),
          "item_type": one of "idea","task","note","reference","journal",
          "actionable": boolean (true if it describes something to act on),
          "tags": array of { "name": string, "kind": one of "topic","person","project","entity" }
        }
        """;

    public static string BuildUserPrompt(string rawText) => rawText;
}
