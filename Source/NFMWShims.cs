// XNA types the ported sources name but the abstraction has no reason to provide.

#if NFMW
using System;

namespace Apos.Shapes {
    /// <summary>
    /// The one XNA helper the shape code reaches for, which is <see cref="Math.Clamp(float, float, float)"/>
    /// under another name. The upstream builds get the real XNA type; this stands in for it so the
    /// shared sources need no change at the call sites.
    /// </summary>
    internal static class MathHelper {
        /// <summary>Clamps a value to the range between a minimum and a maximum, both inclusive.</summary>
        /// <param name="value">The value to clamp.</param>
        /// <param name="min">The lower bound.</param>
        /// <param name="max">The upper bound.</param>
        /// <returns>The clamped value.</returns>
        public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    }

    /// <summary>
    /// How a texture draw flips the source rectangle. XNA's type, carried here because the
    /// <c>Draw</c> overloads that take it are part of the public surface and their callers pass
    /// these flags.
    /// </summary>
    [Flags]
    public enum SpriteEffects {
        /// <summary>Draw the texture as it is stored.</summary>
        None = 0,
        /// <summary>Draw it mirrored left to right.</summary>
        FlipHorizontally = 1,
        /// <summary>Draw it mirrored top to bottom.</summary>
        FlipVertically = 2,
    }
}
#endif
