namespace StatementImporter.Contracts.Messaging;

public sealed record ImportRequested(Guid ImportId);

public sealed record ProcessChunk(Guid ImportId, int ChunkIndex);
