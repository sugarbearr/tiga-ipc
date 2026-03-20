using Microsoft.Extensions.Options;

namespace TigaIpc.IO;

internal static class TigaMemoryMappedFileFactory
{
    internal static ITigaMemoryMappedFile Create(
        string name,
        MappingType type,
        IOptions<TigaIpcOptions>? suppliedOptions,
        out IOptions<TigaIpcOptions> resolvedOptions
    )
    {
        resolvedOptions =
            suppliedOptions ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions());
        var optionsValue = resolvedOptions.Value ?? new TigaIpcOptions();
        return new WaitFreeMemoryMappedFile(name, type, optionsValue);
    }
}
