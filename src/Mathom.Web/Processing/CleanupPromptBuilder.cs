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

    /// <summary>
    /// JSON Schema for the cleanup result, matching the fields <see cref="CleanupResultParser"/>
    /// reads. Used by providers that require <c>response_format: json_schema</c> (e.g. Infomaniak).
    /// </summary>
    public static object ResponseSchema() => new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            clean_text = new { type = "string" },
            item_type = new { type = "string", @enum = new[] { "idea", "task", "note", "reference", "journal" } },
            actionable = new { type = "boolean" },
            tags = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        kind = new { type = "string", @enum = new[] { "topic", "person", "project", "entity" } },
                    },
                    required = new[] { "name", "kind" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "title", "clean_text", "item_type", "actionable", "tags" },
        additionalProperties = false,
    };
}
