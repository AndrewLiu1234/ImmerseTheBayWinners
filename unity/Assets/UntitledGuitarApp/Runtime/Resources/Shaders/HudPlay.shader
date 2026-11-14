// HudPlay: the one shader behind every non-text shape of the play-along HUD (beat dots, strum arrows, cursor, approach
// rings). Unlit and additive: colour = vertex colour * _Color, added to what is already there, scaled by alpha. On the
// additive glasses that is exactly "light on black"; nothing here can draw anything dark.
//
// NEVER COMPILED. This file was written without a Unity editor (offline worker) and has not been through Unity's shader
// compiler. It is kept deliberately minimal for that reason. If it fails to compile or is unsupported, HudShape falls back to
// the built-in "Sprites/Default" shader (same vertex colour * _Color maths, alpha-blended) and logs "hudplay_material".
//
// SOURCES (offline documentation of Unity 2021.3.3f1: Unity.app/Contents/Documentation/en/Manual):
//  * SL-VertexFragmentShaderExamples.html: structure of an unlit vertex/fragment shader (CGPROGRAM, #pragma vertex/fragment,
//    #include "UnityCG.cginc", UnityObjectToClipPos, SV_POSITION, SV_Target).
//  * SL-Blend.html (Blend SrcAlpha One = additive scaled by alpha), SL-ZWrite.html, SL-ZTest.html, SL-Cull.html.
//  * SL-SubShaderTags.html ("Queue" = "Transparent", "RenderType", "IgnoreProjector").
//  * UnityObjectToClipPos(float4): Unity.app/Contents/CGIncludes/UnityShaderUtilities.cginc, line 53.
//
// ZTest Always + ZWrite Off: HUD shapes are never hidden by the virtual neck or the ghost hands, the same way the built-in
// font shader draws HudText. Cull Off: a shape is visible from both sides, so a mirrored (left-handed) parent cannot hide it.
Shader "UntitledGuitarApp/HudPlay"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

        Blend SrcAlpha One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
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
