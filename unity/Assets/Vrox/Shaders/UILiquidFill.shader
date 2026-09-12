// A UI fill that reads as liquid in a vial: the top edge is a moving surface
// rather than a straight cut.
//
// The wave only ever carves *downward* from the fill line. That is not a style
// choice — an Image set to Filled has already cut the quad's geometry at
// fillAmount, so anything the shader draws above that line is clipped by the
// mesh and crests would come out flat-topped. Biasing the wave so its crests
// touch the cut and its troughs hang below keeps every pixel inside the
// geometry, and looks the same as a centred wave one amplitude lower.
//
// The wave also flattens out at both ends of the range. Full and empty are the
// two states a bar spends most of its life in, and they are the two where a
// wave is wrong: at the top there is no headroom to carve into without the bar
// reading as less than full, and at the bottom the amplitude exceeds the
// remaining liquid and breaks the surface into blobs sitting on the floor.
//
// Vertical fill only. On a horizontally filled Image the surface would still
// run along the top while the geometry is cut down the side, which reads as a
// bar that empties in the wrong direction rather than as a bug — so it is
// stated here instead of being silently handled.
//
// No Fallback, on purpose: a fallback turns a compile error into a plausible
// flat bar, which is far worse to debug than magenta.
Shader "Vrox/UI Liquid Fill"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}

        [Header(Gradient)]
        _ColorBottom ("Colour Bottom", Color) = (0.55, 0.05, 0.12, 1)
        _ColorTop ("Colour Top", Color) = (0.95, 0.20, 0.28, 1)

        // A plain 0/1 float, not a shader keyword. Keyword variants can be
        // stripped from a build and fall back to the other branch silently; a
        // float cannot, and this shader is far too small for the cost to matter.
        //
        // Off: the gradient is fixed to the rect, so a colour sits at the same
        // height whatever the fill — the bar reads as a window onto something
        // that does not move.
        // On: the gradient is fixed to the liquid, so the top colour rides the
        // surface down as it drains — the bar reads as a volume of liquid.
        [Toggle] _GradientRidesSurface ("Gradient Rides The Surface", Float) = 1

        [Header(Fill)]
        // Written every frame by LiquidFill from Image.fillAmount. Authored here
        // too, so the material previews as something other than empty and so the
        // inspector slider still drives it with no component attached.
        _Fill ("Fill", Range(0, 1)) = 1

        // Rect size in local units, also written by LiquidFill. The height is
        // what turns pixel amplitudes and pixel-wide antialiasing into the
        // 0..1 space the fill math works in.
        _RectSize ("Rect Size", Vector) = (100, 20, 0, 0)

        // The sprite's outer UV rect (xMin, yMin, xMax, yMax). Atlased sprites
        // do not get 0..1 UVs, and without this the wave's phase and the
        // surface position both shift depending on where the packer happened to
        // put the sprite.
        _SpriteUV ("Sprite UV Rect", Vector) = (0, 0, 1, 1)

        [Header(Surface)]
        _WaveAmplitude ("Wave Amplitude (px)", Range(0, 40)) = 4
        // In cycles across the full width, so it is independent of bar size.
        _WaveFrequency ("Wave Cycles Across", Range(0.25, 12)) = 2.5
        _WaveSpeed ("Wave Speed", Range(0, 8)) = 1.2

        // How much of each end of the range the wave fades out across. Set both
        // to 0 for a surface that waves at every fill, including a full bar.
        _CalmBelow ("Calm Below Fill", Range(0, 0.5)) = 0.12
        _CalmAbove ("Calm Above Fill", Range(0, 0.5)) = 0.08

        _SurfaceColor ("Surface Colour", Color) = (1, 1, 1, 0.85)
        _SurfaceThickness ("Surface Thickness (px)", Range(0, 12)) = 2

        // Standard UI material plumbing. Masks and RectMask2D drive these; a UI
        // shader without them stops being maskable.
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            fixed4 _ColorBottom;
            fixed4 _ColorTop;
            float _GradientRidesSurface;

            float _Fill;
            float4 _RectSize;
            float4 _SpriteUV;
            float _WaveAmplitude;
            float _WaveFrequency;
            float _WaveSpeed;
            float _CalmBelow;
            float _CalmAbove;
            fixed4 _SurfaceColor;
            float _SurfaceThickness;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(o.worldPosition);
                o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                // Image.color, which stays the overall tint on top of the
                // gradient — one place to flash the whole bar white on a hit.
                o.color = v.color;
                return o;
            }

            // Height of the liquid surface, in 0..1 of the rect, at a given
            // horizontal position.
            //
            // Three sines with incommensurate frequencies and speeds. Two would
            // still visibly repeat over a few seconds, which on a bar the player
            // stares at while waiting to heal is the thing that gives it away as
            // a loop.
            float SurfaceAt(float x, float amp)
            {
                float t = _Time.y * _WaveSpeed;
                float w = _WaveFrequency * 6.2831853;

                float h = sin(x * w + t)
                        + 0.55 * sin(x * w * 1.71 - t * 1.29 + 1.7)
                        + 0.30 * sin(x * w * 2.63 + t * 0.61 + 4.1);

                // The three terms sum to at most 1.85, so normalising by that
                // keeps the crest exactly on the fill line at its peak.
                h /= 1.85;

                // Crests reach _Fill, troughs hang one full amplitude below it.
                return _Fill - amp + amp * h;
            }

            // Fades the wave out at both ends of the fill range.
            //
            // A guard of 0 has to mean "never calm", not "always calm", or
            // turning the feature off would flatten the surface everywhere —
            // which is why each side tests the guard rather than relying on the
            // division.
            float Amplitude(float height)
            {
                float amp = _WaveAmplitude / max(height, 1e-3);

                if (_CalmBelow > 0.0)
                {
                    amp *= saturate(_Fill / _CalmBelow);
                }
                if (_CalmAbove > 0.0)
                {
                    amp *= saturate((1.0 - _Fill) / _CalmAbove);
                }
                return amp;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Back out of the sprite's atlas placement into rect-local 0..1.
                float2 span = max(_SpriteUV.zw - _SpriteUV.xy, 1e-6);
                float2 n = (i.texcoord - _SpriteUV.xy) / span;

                float height = max(_RectSize.y, 1e-3);
                float amp = Amplitude(height);
                float surface = SurfaceAt(n.x, amp);

                // Signed distance below the surface, in pixels. One pixel of
                // feathering is what keeps the crests from stair-stepping as
                // they slide across the bar.
                float d = (surface - n.y) * height;
                float inside = saturate(d + 0.5);

                // Gradient position. Riding the surface divides by the local
                // surface height, so the top colour follows every crest and
                // trough rather than sitting at a fixed line the wave crosses.
                float g = lerp(n.y, n.y / max(surface, 1e-3), _GradientRidesSurface);
                fixed4 liquid = lerp(_ColorBottom, _ColorTop, saturate(g));

                half4 col = (tex2D(_MainTex, i.texcoord) + _TextureSampleAdd)
                          * i.color * liquid;

                // A brighter band riding the surface — the meniscus. Lerped
                // rather than blended so it survives a dark liquid colour.
                float band = 1.0 - saturate(abs(d) / max(_SurfaceThickness, 1e-3));
                col.rgb = lerp(col.rgb, _SurfaceColor.rgb, band * _SurfaceColor.a);

                col.a *= inside;

                // An empty bar draws nothing at all, rather than a one-pixel
                // line of surface sitting on the floor.
                col.a *= step(1e-4, _Fill);

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(col.a - 0.001);
                #endif

                return col;
            }
            ENDCG
        }
    }
}
