namespace SR2MP.Client.Models;

/// <summary>Chooses a camera-visible layer for remote avatar renderers.</summary>
internal static class RemotePlayerRenderLayerPolicy
{
    internal static int ResolveRendererLayer(int sourceLayer, int cameraCullingMask, int defaultLayer)
    {
        if (IsIncludedByCamera(sourceLayer, cameraCullingMask))
            return sourceLayer;

        return IsIncludedByCamera(defaultLayer, cameraCullingMask) ? defaultLayer : sourceLayer;
    }

    private static bool IsIncludedByCamera(int layer, int cameraCullingMask)
        => layer is >= 0 and < 32 && (cameraCullingMask & (1 << layer)) != 0;

#if SR2MP_TEST
    internal static bool VerifyRules()
        => ResolveRendererLayer(sourceLayer: 8, cameraCullingMask: 1 << 8, defaultLayer: 0) == 8 &&
           ResolveRendererLayer(sourceLayer: 8, cameraCullingMask: 1 << 0, defaultLayer: 0) == 0 &&
           ResolveRendererLayer(sourceLayer: 8, cameraCullingMask: 1 << 8, defaultLayer: -1) == 8 &&
           ResolveRendererLayer(sourceLayer: -1, cameraCullingMask: 1 << 0, defaultLayer: 0) == 0;
#endif
}
