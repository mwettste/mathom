namespace Mathom.Web.Embeddings;

/// <summary>
/// Single source of truth for the embedding vector dimension. The pgvector column,
/// its migration, and tests all reference this — a pgvector column has a fixed dimension,
/// so changing models with a different size requires a new additive migration.
/// </summary>
public static class EmbeddingConfig
{
    // Confirmed from the Infomaniak embeddings model in Task 1's verification step.
    public const int Dimensions = 1024;
}
