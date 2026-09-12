// Flat ground: the tile's biome colour and nothing else.
//
// The counterpart to Vrox/Terrain Checkerboard. An authored biome has a colour
// somebody chose, and drawing a pattern over it would mean the map never looks
// the way the biome catalogue says it does. The checkerboard stays for biomes
// nobody has authored yet, where the point is to be obviously unfinished.
//
// Opaque and depth-writing, matching the checkerboard exactly — the two are
// drawn into the same scene at the same depth, and a mismatch would let one sort
// over the other along the seams between authored and unauthored ground.
//
// No Fallback, on purpose: a fallback turns a compile error into a flat colour
// that looks like a working shader, which is what this shader already is.
//
// URP, with the pass deliberately left untagged. An untagged pass is
// SRPDefaultUnlit, which both the 2D renderer this project uses and the ordinary
// forward renderer draw — DrawRenderer2DPass and DrawObjectsPass each list that
// tag. Naming LightMode Universal2D would tie the ground to the 2D renderer and
// make it vanish if the pipeline asset were ever pointed at the other one.
Shader "Vrox/Terrain Flat"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
            "RenderPipeline" = "UniversalPipeline"
        }
        ZWrite On

        Pass
        {
            Name "Unlit"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                // Carried on a TEXCOORD rather than COLOR. A COLOR interpolator
                // is allowed to clamp to [0,1] on some targets, and while a biome
                // tint never leaves that range today, the clamp would be silent
                // if one ever did.
                half4 tint : TEXCOORD0;
            };

            // Every material property has to sit in this one buffer, in one
            // block, for the SRP batcher to accept the shader. A property
            // declared outside it does not fail — it quietly drops this material
            // out of batching, which shows up as a performance change nobody can
            // trace back to a shader edit.
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                // The biome colour rides on the vertex, because a run of merged
                // tiles is one quad and the colour has to vary per run rather
                // than per material.
                o.tint = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = i.tint * _Tint;
                c.a = 1;
                return c;
            }
            ENDHLSL
        }
    }
}
