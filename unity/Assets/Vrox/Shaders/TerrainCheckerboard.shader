// The checkerboard, tinted per tile by the mesh's vertex colours.
//
// Vrox/Checkerboard is a background: one flat pair of colours across an entire
// quad. Terrain needs the same pattern taking its colour from the biome under
// it, which a background shader has no way to express — so this reads COLOR off
// the vertex and shades the checker around it.
//
// Opaque with depth writes, unlike the sprite material the entity meshes use.
// That is what makes draw order against them correct by construction rather than
// by sort luck: terrain fills depth first, and anything nearer than it survives.
//
// There is deliberately no Fallback, for the reason given in Checkerboard.shader:
// a fallback turns a compile error into a plausible flat colour.
//
// URP, with the pass deliberately left untagged. An untagged pass is
// SRPDefaultUnlit, which both the 2D renderer this project uses and the ordinary
// forward renderer draw — DrawRenderer2DPass and DrawObjectsPass each list that
// tag. Naming LightMode Universal2D would tie the ground to the 2D renderer and
// make it vanish if the pipeline asset were ever pointed at the other one.
Shader "Vrox/Terrain Checkerboard"
{
    Properties
    {
        // Multipliers against the vertex colour, not colours in their own right.
        // A biome's identity has to survive the pattern, so the checker shades
        // the tile rather than replacing it.
        _CheckLight ("Light Check Tint", Color) = (1.10, 1.10, 1.10, 1)
        _CheckDark ("Dark Check Tint", Color) = (0.88, 0.88, 0.90, 1)

        // Checks per world unit. One tile is one unit, so 0.5 puts a check every
        // two tiles and 1 puts one on every tile.
        _Scale ("Checks Per Unit", Float) = 0.5

        // Zero by default: ground that slides under your feet reads as the world
        // moving, not as texture.
        _Scroll ("Scroll Speed XY", Vector) = (0, 0, 0, 0)

        [Header(Wave)]
        _WaveAmplitude ("Wave Amplitude", Range(0, 4)) = 0
        _WaveFrequency ("Wave Frequency", Range(0.05, 5)) = 0.6
        _WaveSpeed ("Wave Speed", Range(0, 8)) = 1

        [Header(Melt)]
        _MeltAmount ("Melt Amount", Range(0, 4)) = 0
        _MeltScale ("Melt Scale", Range(0.02, 2)) = 0.25
        _MeltDroop ("Melt Droop", Range(0, 4)) = 1
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
            // 3.0 guarantees the derivative instructions the antialiasing needs.
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 coord : TEXCOORD0;
                // Carried on a TEXCOORD rather than COLOR. A COLOR interpolator
                // is allowed to clamp to [0,1] on some targets, and while a biome
                // tint never leaves that range today, the clamp would be silent
                // if one ever did.
                half4 tint : TEXCOORD1;
            };

            // Every material property has to sit in this one buffer, in one
            // block, for the SRP batcher to accept the shader. A property
            // declared outside it does not fail — it quietly drops this material
            // out of batching, which shows up as a performance change nobody can
            // trace back to a shader edit. It matters most here: the terrain is
            // the largest thing drawn.
            CBUFFER_START(UnityPerMaterial)
                half4 _CheckLight;
                half4 _CheckDark;
                float _Scale;
                float4 _Scroll;
                float _WaveAmplitude;
                float _WaveFrequency;
                float _WaveSpeed;
                float _MeltAmount;
                float _MeltScale;
                float _MeltDroop;
            CBUFFER_END

            // Identical to Vrox/Checkerboard's warp. Kept as a copy rather than
            // shared through an .hlsl because the two shaders are free to
            // diverge — this one is ground and that one is a backdrop.
            float2 Warp(float2 p, float t)
            {
                float2 q = p;

                if (_MeltAmount > 0.0)
                {
                    float2 m = float2(
                        sin(p.y * _MeltScale + t * 0.37),
                        cos(p.x * _MeltScale * 0.83 - t * 0.29));
                    q += _MeltAmount * m;
                    q.y -= _MeltDroop * _MeltAmount * (0.5 + 0.5 * m.x);
                }

                if (_WaveAmplitude > 0.0)
                {
                    p += _WaveAmplitude * float2(
                        sin(q.y * _WaveFrequency + t),
                        sin(q.x * _WaveFrequency * 1.17 + t * 1.31));
                }

                return p;
            }

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);

                // Always world XY. The terrain mesh carries no UVs — it is built
                // from run-merged quads whose widths vary — and world space is
                // what makes the pattern continuous across a merge instead of
                // restarting at every quad edge.
                o.coord = TransformObjectToWorld(v.vertex.xyz).xy;
                o.tint = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 p = i.coord * _Scale + _Scroll.xy * _Time.y;
                p = Warp(p, _Time.y * _WaveSpeed);

                // The analytic box-filtered square wave, as in the background
                // shader: a hard checkerboard shimmers once the squares approach
                // pixel size, and a 128-tile map seen from a distance is exactly
                // that case.
                float2 w = fwidth(p) + 1e-5;
                float2 a = 2.0 * (abs(frac((p - 0.5 * w) * 0.5) - 0.5)
                                - abs(frac((p + 0.5 * w) * 0.5) - 0.5)) / w;

                float checker = 0.5 - 0.5 * a.x * a.y;

                half4 shade = lerp(_CheckDark, _CheckLight, checker);
                return half4(i.tint.rgb * shade.rgb, 1);
            }
            ENDHLSL
        }
    }
}
