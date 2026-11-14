// NeckOverlay: unlit ADDITIVE light for the glasses (black = invisible, so only light exists). One shader draws every line
// and dot of the neck: soft-edged rectangles (strings, frets, nut) and soft-edged discs or rings (fingertip dots, glows, O
// marks). Load it with Resources.Load<Shader>("Shaders/NeckOverlay") (Shader.Find fails in builds).
//
// Vertex data (written by Runtime/Neck/NeckGeometry.cs; a mesh without these channels draws as a plain solid quad):
//   COLOR      colour, already multiplied by its brightness
//   TEXCOORD0  xy = position inside the quad, -1..1;  z = 0 rectangle, 1 disc;  w = ring inner radius 0..1 (negative = filled)
//   TEXCOORD1  x = gain group 0..12 (0 = fixed; n picks _GainA.xyzw, _GainB.xyzw, _GainC.xyzw in that order);
//              y = edge softness 0..1 (0 = one-pixel anti-aliasing only)
// The gain groups let a script fade one finger's dot or one string by setting a material vector, with no mesh rebuild.
// For a soft disc on any plain quad (Unity's built-in Quad has UVs 0..1): set _Disc = 1 and _UV01 = 1.
//
// No shader keywords on purpose. Our materials are created from code, and for "shader feature" keywords Unity "only
// compiles variants for keywords that are in use. A keyword is in use if a material that is included in the build has that
// keyword enabled" (Manual/shader-keywords.html): a keyword switched on only from code would be missing from the build.
//
// Sources, Unity 2021.3.3f1 offline docs (/Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/):
//   Manual/SL-Blend.html              "Blend One One // Additive" (Common blend types)
//   Manual/SL-ZWrite.html             ZWrite Off: "disabled for semi-transparent" geometry
//   Manual/SL-Cull.html               Cull Off: both faces (the neck is mirrored for left-handed players, which flips winding)
//   Manual/SL-Properties.html         name ("display text", Range(a, b) | Color | Vector) = default
//   Manual/SL-SubShaderTags.html      Queue, RenderType, IgnoreProjector, PreviewType
//   Manual/SL-VertexProgramInputs.html  POSITION, COLOR, TEXCOORD0, TEXCOORD1
//   CGIncludes/UnityShaderUtilities.cginc  UnityObjectToClipPos;  CGIncludes/UnityCG.cginc  GammaToLinearSpace
//   Structure copied from the editor's own template, Resources/ScriptTemplates/84-Shader__Unlit Shader-NewUnlitShader.shader.txt
Shader "UntitledGuitarApp/NeckOverlay"
{
    Properties
    {
        _Color ("Colour", Color) = (1,1,1,1)
        _Intensity ("Intensity", Range(0.0, 4.0)) = 1.0
        _Disc ("Soft disc on every quad (0 = shape from the mesh)", Range(0.0, 1.0)) = 0.0
        _Softness ("Minimum edge softness (0 = 1 px anti-aliasing)", Range(0.0, 1.0)) = 0.0
        _UV01 ("UVs run 0..1 (Unity quad) instead of -1..1", Range(0.0, 1.0)) = 0.0
        _GainA ("Gain of groups 1-4", Vector) = (1,1,1,1)
        _GainB ("Gain of groups 5-8", Vector) = (1,1,1,1)
        _GainC ("Gain of groups 9-12", Vector) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        LOD 100

        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 shape : TEXCOORD0;
                float4 extra : TEXCOORD1;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float4 shape : TEXCOORD0;
                float softness : TEXCOORD1;
            };

            float4 _Color;
            float _Intensity;
            float _Disc;
            float _Softness;
            float _UV01;
            float4 _GainA;
            float4 _GainB;
            float4 _GainC;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                // Gain group -> gain. Group 0 (and a mesh with no TEXCOORD1) keeps gain 1.
                float grp = v.extra.x;
                float4 pickA = saturate(1.0 - abs(float4(1.0, 2.0, 3.0, 4.0) - grp));
                float4 pickB = saturate(1.0 - abs(float4(5.0, 6.0, 7.0, 8.0) - grp));
                float4 pickC = saturate(1.0 - abs(float4(9.0, 10.0, 11.0, 12.0) - grp));
                float gain = 1.0 + dot(pickA, _GainA - 1.0) + dot(pickB, _GainB - 1.0) + dot(pickC, _GainC - 1.0);

                float3 rgb = v.color.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = GammaToLinearSpace(rgb);   // vertex colours are authored in sRGB; _Color is converted by Unity
                #endif
                o.color = float4(rgb * _Color.rgb * (_Intensity * max(gain, 0.0)), v.color.a * _Color.a);

                o.shape = v.shape;
                o.shape.xy = lerp(v.shape.xy, v.shape.xy * 2.0 - 1.0, _UV01);
                o.softness = max(v.extra.y, _Softness);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.shape.xy;
                float softness = i.softness;

                // Rectangle: full inside, fading to nothing at the quad's edge. Across the line (y) the fade is at least one
                // pixel wide (fwidth) or the requested softness; along it (x) it is one pixel, at capped ends only.
                float2 inside = 1.0 - abs(p);
                float2 aa = max(fwidth(p), 0.00001);
                float rectMask = saturate(inside.x / aa.x) * saturate(inside.y / max(aa.y, softness));

                // Disc or ring: the same, on the distance from the centre.
                float r = length(p);
                float aaR = max(fwidth(r), 0.00001);
                float discMask = saturate((1.0 - r) / max(aaR, softness));
                discMask = discMask * discMask * (3.0 - 2.0 * discMask);
                discMask *= saturate((r - i.shape.w) / aaR);   // the hole of a ring; a negative inner radius leaves the disc filled

                float mask = lerp(rectMask, discMask, saturate(max(i.shape.z, _Disc)));
                return fixed4(i.color.rgb * mask, i.color.a * mask);
            }
            ENDCG
        }
    }
}
