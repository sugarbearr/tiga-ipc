using MessagePack;

namespace TigaIpc
{
    internal static class MessagePackOptions
    {
        internal static MessagePackSerializerOptions Instance { get; } =
            MessagePackSerializerOptions.Standard
                .WithResolver(CompositeResolver.Instance);
    }
}