// An unlit scrolling checkerboard, for any mesh — a quad, a plane, a cube.
//
// There is deliberately no Fallback. A fallback turns a compile error into a
// flat colour that looks like a working shader with bad settings, which is a
// much worse thing to debug than magenta.
//
// URP, with the pass deliberately left untagged. An untagged pass is
// SRPDefaultUnlit, which both the 2D renderer this project uses and the ordinary
// forward renderer draw — DrawRenderer2DPass and DrawObjectsPass each list that
// tag. Naming LightMode Universal2D would tie this to the 2D renderer and make
// it vanish if the pipeline asset were ever pointed at the other one.
Shader "Vrox/Checkerboard"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.10, 0.10, 0.13, 1)
        _ColorB ("Color B", Color) = (0.16, 0.16, 0.21, 1)

        // Checks per unit. A default Unity Quad is 1x1, so anything below 1 puts
        // less than a whole check on it and it reads as a solid colour.
        _Scale ("Checks Per Unit", Float) = 1

        _Scroll ("Scroll Speed XY", Vector) = (0.15, 0.08, 0, 0)

        // A plain 0/1 float, not a shader keyword. Keyword variants can be
        // stripped from a build and fall back to the other branch silently; a
        // float cannot, and this shader is far too small for the cost to matter.
        [Toggle] _WorldSpace ("Anchor To World", Float) = 1

        [Header(Wave)]
        // How far the pattern is pushed sideways, in units. 0 is a flat grid.
        _WaveAmplitude ("Wave Amplitude", Range(0, 4)) = 0
        _WaveFrequency ("Wave Frequency", Range(0.05, 5)) = 0.6
        _WaveSpeed ("Wave Speed", Range(0, 8)) = 1

        [Header(Melt)]
        // Warps the wave itself rather than the pattern. One sine over another is
        // what turns a regular ripple into something that pools and smears.
        _MeltAmount ("Melt Amount", Range(0, 4)) = 0
        _MeltScale ("Melt Scale", Range(0.02, 2)) = 0.25
        // Downward bias, so the distortion reads as sagging rather than drifting.
        _MeltDroop ("Melt Droop", Range(0, 4)) = 1
    }

    SubShader
    {
        // An ordinary opaque material, so it behaves like any other 3D surface.
        // For a background, put the object behind everything rather than making
        // the shader special.
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
            "RenderPipeline" = "UniversalPipeline"
        }

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
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 coord : TEXCOORD0;
            };

            // Every material property has to sit in this one buffer, in one
            // block, for the SRP batcher to accept the shader. A property
            // declared outside it does not fail — it quietly drops this material
            // out of batching, which shows up as a performance change nobody can
            // trace back to a shader edit.
            CBUFFER_START(UnityPerMaterial)
                half4 _ColorA;
                half4 _ColorB;
                float _Scale;
                float4 _Scroll;
                float _WorldSpace;
                float _WaveAmplitude;
                float _WaveFrequency;
                float _WaveSpeed;
                float _MeltAmount;
                float _MeltScale;
                float _MeltDroop;
            CBUFFER_END

            // Bends the plane the checkerboard is drawn on.
            //
            // Applied to the sample coordinate, not to the geometry, so a flat
            // quad appears to ripple without any extra vertices.
            //
            // The antialiasing downstream needs no changes for this: it measures
            // the derivative of the *final* coordinate, so wherever the warp
            // stretches the pattern the filter widens to match and heavy melt
            // stays smooth instead of tearing into aliasing.
            float2 Warp(float2 p, float t)
            {
                float2 q = p;

                // Warping the input to the wave, rather than adding a second
                // wave to the output. Sines added together stay obviously
                // periodic; a sine whose argument is itself waving does not.
                if (_MeltAmount > 0.0)
                {
                    float2 m = float2(
                        sin(p.y * _MeltScale + t * 0.37),
                        cos(p.x * _MeltScale * 0.83 - t * 0.29));
                    q += _MeltAmount * m;

                    // Sag proportional to the melt, so troughs hang lower than
                    // crests rise. Symmetric distortion reads as water; this
                    // reads as something losing its shape.
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

                // World XY, so one quad reads as infinite ground: a background
                // parented to the camera would otherwise have the pattern glued
                // to the screen and never appear to move.
                float2 world = TransformObjectToWorld(v.vertex.xyz).xy;
                o.coord = lerp(v.uv, world, _WorldSpace);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 p = i.coord * _Scale + _Scroll.xy * _Time.y;
                p = Warp(p, _Time.y * _WaveSpeed);

                // Antialiased rather than a plain step. A hard checkerboard
                // shimmers violently once the squares approach pixel size, which
                // for a scrolling background is most of the time. This is the
                // analytic box-filtered square wave: it integrates the pattern
                // across the area each pixel actually covers, so distant checks
                // settle to flat grey instead of crawling.
                float2 w = fwidth(p) + 1e-5;
                float2 a = 2.0 * (abs(frac((p - 0.5 * w) * 0.5) - 0.5)
                                - abs(frac((p + 0.5 * w) * 0.5) - 0.5)) / w;

                // The checker is the exclusive-or of the two axes, which the
                // product of two square waves in [-1,1] expresses directly.
                float checker = 0.5 - 0.5 * a.x * a.y;

                return lerp(_ColorA, _ColorB, checker);
            }
            ENDHLSL
        }
    }
}
