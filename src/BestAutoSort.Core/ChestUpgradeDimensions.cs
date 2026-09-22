using System;

namespace BestAutoSort.Core;

/// <summary>
/// Legacy chest dimension resolution (issue #8).
/// BestAutoSort may enforce the minimum capacity required by its own tier,
/// but it must never shrink a larger runtime inventory already applied by
/// another mod (e.g. ValheimQoL). Existing item positions stay valid because
/// the result is never smaller than the current runtime size.
/// </summary>
public static class ChestUpgradeDimensions
{
    public static void ResolveLegacyDimensions(
        int currentWidth,
        int currentHeight,
        int templateWidth,
        int templateHeight,
        out int finalWidth,
        out int finalHeight)
    {
        finalWidth = Math.Max(currentWidth, templateWidth);
        finalHeight = Math.Max(currentHeight, templateHeight);
    }
}
