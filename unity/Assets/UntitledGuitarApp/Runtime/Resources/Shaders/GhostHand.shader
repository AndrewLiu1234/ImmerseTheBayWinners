// UntitledGuitarApp/GhostHand: the look of every ghost hand (the procedural capsule hand and the Blender hand).
// Built-in render pipeline, unlit, ADDITIVE. The glasses add light to the real world, so black is invisible and
// the hand is made of light: a bright fresnel rim that carries the silhouette, a faint fill the real strings show
// through, and fingertips that are the brightest thing in view. No dark outline anywhere.
//
// Load it with Resources.Load<Shader>("Shaders/GhostHand") (Shader.Find does not work for it in a build).
//
// VERTEX COLOUR CONVENTION (the same for every hand mesh, skinned or not; alpha is ignored):
//   red   minus green = fingertip glow   -> paint the fingertips PURE RED    (1, 0, 0)
//   blue  minus green = fade out         -> paint parts to hide PURE BLUE    (0, 0, 1)   (soft ends of a mesh)
//   any grey (white, black, in between)  = plain hand.
//   So a mesh with no painted colours renders as a plain hand whatever default colour the importer gives it.
//
// PER-RENDERER VALUES (set them with a MaterialPropertyBlock, the material stays shared):
//   _Emphasis  1 = this part at full brightness, 0.3 = dimmed (finger-by-finger mode)
//   _Fade      1 = normal, 0.4 = "fade to 40% after success"
//   _TipColor  optional per-finger fingertip colour
//
// STEREO: nothing here depends on the screen, on a grab pass or on a depth texture. The view direction comes from
// _WorldSpaceCameraPos, which Unity sets for each camera, so two ordinary cameras drawing the left and right halves
// of one display each get their own correct rim.
//
// SKINNED MESHES: only UnityObjectToClipPos / unity_ObjectToWorld / UnityObjectToWorldNormal touch the vertex, so the
// shader works unchanged on a SkinnedMeshRenderer (Unity skins the vertices before this vertex program runs).
//
// SOURCES (Unity 2021.3 manual, local copy under Unity.app/Contents/Documentation/en/Manual, online at
// https://docs.unity3d.com/2021.3/Documentation/Manual/<page>.html), and the include files that ship with
// Unity 2021.3.3f1 under Unity.app/Contents/CGIncludes:
//   SL-Properties              property syntax: Color, Range(min, max), Float
//   SL-PropertiesInPrograms    "Color and Vector properties map to float4, half4 or fixed4", "Range and Float properties
//                              map to float, half or fixed"; "When using Linear color space, all material color
//                              properties are supplied as sRGB colors, but are converted into linear values"
//   SL-SubShaderTags           "Queue" = "Transparent", "RenderType", "IgnoreProjector" = "True"
//   SL-Blend                   "Blend One One // Additive"
//   SL-ZWrite                  "ZWrite Off: Disables writing to the depth buffer"
//   SL-Cull                    "Cull Back ... This is the default value"
//   SL-VertexProgramInputs     POSITION / NORMAL / COLOR inputs; a "fixed4 color : COLOR" interpolator
//   SL-VertexFragmentShaderExamples   "Unlit/SkyReflection Per Pixel": worldPos + worldNormal interpolators and
//                              normalize(UnityWorldSpaceViewDir(i.worldPos)) in the fragment program
//   UnityShaderUtilities.cginc:44   inline float4 UnityObjectToClipPos(in float3 pos)  (+ float4 overload, line 53)
//   UnityCG.cginc:177          inline float3 UnityObjectToWorldNormal(in float3 norm)
//   UnityCG.cginc:225          inline float3 UnityWorldSpaceViewDir(in float3 worldPos)  = _WorldSpaceCameraPos.xyz - worldPos
//   UnityCG.cginc:105, 126     inline half3 GammaToLinearSpace(half3), inline half3 LinearToGammaSpace(half3)
//   UnityShaderVariables.cginc:46, 165   float3 _WorldSpaceCameraPos;  float4x4 unity_ObjectToWorld;
Shader "UntitledGuitarApp/GhostHand"
{
    Properties
    {
        _Color ("Fill colour (electric blue #4FC3F7)", Color) = (0.310, 0.765, 0.969, 1)
        _RimColor ("Rim colour (brighter blue #B3E5FC)", Color) = (0.702, 0.898, 0.988, 1)
        _TipColor ("Fingertip glow colour", Color) = (0.86, 0.96, 1.0, 1)
        _Fill ("Fill strength (0.15 - 0.25)", Range(0.0, 1.0)) = 0.2
        _RimStrength ("Rim strength", Range(0.0, 4.0)) = 1.0
        _RimPower ("Rim sharpness (higher = thinner)", Range(0.5, 8.0)) = 3.0
        _TipGlow ("Fingertip glow strength", Range(0.0, 4.0)) = 0.9
        _Brightness ("Overall brightness (tune to the glasses' dimming)", Range(0.0, 4.0)) = 1.0
        _Emphasis ("Emphasis (per renderer)", Range(0.0, 2.0)) = 1.0
        _Fade ("Fade (per renderer)", Range(0.0, 1.0)) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Blend One One
            ZWrite Off
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                fixed4 color : COLOR;
            };

            fixed4 _Color;
            fixed4 _RimColor;
            fixed4 _TipColor;
            float _Fill;
            float _RimStrength;
            float _RimPower;
            float _TipGlow;
            float _Brightness;
            float _Emphasis;
            float _Fade;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // unit normal and unit view direction; the max() keeps a zero-length normal from producing a NaN
                float3 n = i.worldNormal;
                n = n * rsqrt(max(dot(n, n), 1e-8));
                float3 toCamera = UnityWorldSpaceViewDir(i.worldPos);
                toCamera = toCamera * rsqrt(max(dot(toCamera, toCamera), 1e-8));

                // fresnel rim: 0 where the surface faces the camera, 1 on the silhouette
                float ndv = saturate(dot(n, toCamera));
                float rim = pow(saturate(1.0 - ndv), _RimPower);

                // vertex colour masks (see the convention at the top of this file)
                float tip = saturate(i.color.r - i.color.g);
                float cover = 1.0 - saturate(i.color.b - i.color.g);

                // The strengths below are meant as fractions of what the eye sees. In a Linear colour space project Unity
                // hands the colours over in linear light, so bring them back to display values, mix, and convert the result.
                float3 fillColor = _Color.rgb;
                float3 rimColor = _RimColor.rgb;
                float3 tipColor = _TipColor.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                fillColor = LinearToGammaSpace(fillColor);
                rimColor = LinearToGammaSpace(rimColor);
                tipColor = LinearToGammaSpace(tipColor);
                #endif

                float3 c = fillColor * _Fill + rimColor * (rim * _RimStrength) + tipColor * (tip * _TipGlow);
                c = c * (cover * _Emphasis * _Fade * _Brightness);

                #ifndef UNITY_COLORSPACE_GAMMA
                c = GammaToLinearSpace(c);
                #endif

                // additive: the colour is added to what is already there; alpha carries the coverage for captures
                return fixed4(c, saturate(max(c.r, max(c.g, c.b))));
            }
            ENDCG
        }
    }
}
