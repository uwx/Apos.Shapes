using NFMWorld;
using NFMWorld.Shaders;

namespace Apos.Shapes;

/// <summary>
/// The uniforms and textures one shader variant's program resolved to.
///
/// The generated <c>*Parameters</c> type would normally do this job, but the compiler emits one per
/// technique, and apos-shapes has sixteen feature variants that share a uniform layout and differ
/// only in the pixel entry point. Resolving by name against the variant's own reflection gives the
/// batch one type to hold while keeping the same typed wrappers at the call sites - the names are
/// identical in every variant, and a negative slot still means "the compiler dropped it", exactly
/// as <c>SlotOf</c> in a generated bundle reports it.
/// </summary>
internal sealed class VariantParameters
{
    private readonly ShaderReflection _reflection;

    internal VariantParameters(ShaderReflection reflection) => _reflection = reflection;

    internal Float4x4EffectParameter view_projection => new(Slot("view_projection"));
    internal Float2EffectParameter half_viewport => new(Slot("half_viewport"));
    internal FloatEffectParameter dither_scale => new(Slot("dither_scale"));
    internal FloatEffectParameter dither_mode => new(Slot("dither_mode"));
    internal Float2EffectParameter ramp_texel => new(Slot("ramp_texel"));
    internal Float2EffectParameter band_tex_size => new(Slot("band_tex_size"));
    internal Float2EffectParameter band_texel => new(Slot("band_texel"));
    internal Float2EffectParameter curve_texel => new(Slot("curve_texel"));
    internal Float2EffectParameter msdf_texel => new(Slot("msdf_texel"));
    internal FloatEffectParameter pixel_size => new(Slot("pixel_size"));

    internal TextureEffectParameter TextureTex => new(TextureSlot("TextureTex"));
    internal TextureEffectParameter BandTex => new(TextureSlot("BandTex"));
    internal TextureEffectParameter CurveTex => new(TextureSlot("CurveTex"));
    internal TextureEffectParameter MsdfTex => new(TextureSlot("MsdfTex"));
    internal TextureEffectParameter ArcTex => new(TextureSlot("ArcTex"));
    internal TextureEffectParameter BlueNoiseTex => new(TextureSlot("BlueNoiseTex"));
    internal TextureEffectParameter RampTex => new(TextureSlot("RampTex"));

    private int Slot(string name)
    {
        foreach (var uniform in _reflection.Uniforms)
        {
            if (uniform.Name == name) return uniform.Offset;
        }
        return -1;
    }

    private int TextureSlot(string name)
    {
        foreach (var texture in _reflection.Textures)
        {
            if (texture.Name == name) return texture.Slot;
        }
        return -1;
    }
}
