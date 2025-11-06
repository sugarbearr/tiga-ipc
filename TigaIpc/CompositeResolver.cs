using MessagePack;
using MessagePack.Resolvers;

namespace TigaIpc
{
    [CompositeResolver(typeof(AssemblyResolver), typeof(StandardResolver))]
    internal sealed partial class CompositeResolver;
}