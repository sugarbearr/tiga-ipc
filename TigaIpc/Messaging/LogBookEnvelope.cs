using MessagePack;

namespace TigaIpc.Messaging;

[MessagePackObject]
public readonly record struct LogBookEnvelope(
    [property: Key(0)] int SchemaVersion,
    [property: Key(1)] LogBook LogBook
);
