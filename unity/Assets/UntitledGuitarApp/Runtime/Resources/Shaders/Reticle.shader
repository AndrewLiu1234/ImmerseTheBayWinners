// UntitledGuitarApp/Reticle: unlit, additive, per-vertex colour, ignores depth. Only used by Runtime/Input/Reticle.cs, which
// loads it with Resources.Load<Shader>("Shaders/Reticle") (a Resources folder, so player builds include it; Shader.Find would not).
//
// Output = vertex colour * _Color, added to what is already on screen, weighted by alpha:  Blend SrcAlpha One.
// Additive means it can only add light, never hide what is behind it, and black (alpha 0) is invisible, which is what
// the glasses' additive display needs. ZTest Always and the Overlay queue keep it on top of everything. Cull Off makes it
// independent of triangle winding.
//
// STATUS: NEVER COMPILED OR SEEN. Written on 2026-11-14 with no Unity editor available, so no shader compiler has run on this
// file and nobody has looked at its output. It was reviewed by hand against the Unity 2021.3 manual that ships in the
// 2021.3.3f1 install (/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en/Manual/, pages dated 2022-05-27;
// the same pages are at https://docs.unity3d.com/2021.3/Documentation/Manual/<page>.html):
//   SL-Shader.html                  Shader "<name>" { Properties SubShader Fallback } structure
//   SL-Properties.html              _Name ("Display name", Color) = (r, g, b, a)
//   SL-SubShaderTags.html           Tags { "Queue" = "Overlay" ... }, RenderType, IgnoreProjector
//   SL-Cull.html                    Cull Off
//   SL-ZWrite.html                  ZWrite Off
//   SL-ZTest.html                   ZTest Always
//   SL-Blend.html                   Blend <source factor> <destination factor>; factors One and SrcAlpha; "Blend One One // Additive"
//   SL-Fallback.html                Fallback Off
//   SL-VertexProgramInputs.html     the "VertexInputSimple" example: COLOR in v2f, UnityObjectToClipPos, SV_POSITION, SV_Target
//   SL-ShaderSemantics.html         POSITION / COLOR / SV_POSITION / SV_Target
//   Contents/CGIncludes/UnityShaderUtilities.cginc (included by UnityCG.cginc): UnityObjectToClipPos(float4 pos)
// The Tags / ZWrite Off / ZTest Always combination also matches Unity's own TextMeshPro "TMP_SDF Overlay.shader".
// If it fails to compile, Unity prints the error in the Console; Reticle.cs then falls back to Sprites/Default (and warns).
Shader "UntitledGuitarApp/Reticle"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    Fallback Off
}
