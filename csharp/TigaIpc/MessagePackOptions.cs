using MessagePack;
using MessagePack.Resolvers;

namespace TigaIpc
{
    internal static class MessagePackOptions
    {
        internal static MessagePackSerializerOptions Instance { get; } =
#if NET462
            MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);
#else
            MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Instance);
#endif
    }
}
