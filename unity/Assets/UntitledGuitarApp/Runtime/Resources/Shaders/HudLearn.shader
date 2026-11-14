// HudLearn.shader: the marks, icons and tuner dial of the Learn HUD (Runtime/Hud/Learn). Unlit, for an additive display.
//
// !!! THIS SHADER HAS NEVER BEEN COMPILED. It was written offline (no Unity editor was available). If it fails to compile,
// !!! LearnGraphics falls back to the built-in font shader, so the HUD still draws; fix or delete this file.
//
// What it does: output = vertex colour * _Color, with brightness scaled by alpha, blended as "lighten" (the brighter of
// the shape and what is already there). Black therefore adds nothing, which is what the glasses do anyway (black is
// invisible), a fading shape simply gets dimmer, overlapping strokes of one shape do not double up into hot spots (plain
// "Blend One One" addition would), and the result does not depend on draw order. Depth is ignored (ZTest Always), exactly
// like the built-in font shader that HudText uses, so marks and text behave the same.
//
// Loaded with Resources.Load<Shader>("Shaders/HudLearn") (Shader.Find fails in builds). Colour and brightness come per
// renderer from a MaterialPropertyBlock that sets _Color.
//
// SOURCES (Unity 2021.3 offline manual: /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en/Manual):
//  * SL-Blend.html: "finalValue = sourceFactor * sourceValue operation destinationFactor * destinationValue"; "Blend <source
//    factor> <destination factor>"; factor "One: The value of this input is one."
//  * SL-BlendOp.html: "BlendOp <operation> ... Max: Use the larger of source and destination." and "Different graphics APIs
//    handle unsupported blending operations differently: GL skips unsupported operations, and Vulkan and Metal fall back to
//    Add operations." (So the worst case on the Mac is plain additive, which is still correct for this display.)
//  * SL-ZTest.html: "Always: No depth testing occurs. Draw all geometry, regardless of distance."
//  * SL-ZWrite.html: "Off: Disables writing to the depth buffer."
//  * SL-Cull.html: "Off: Do not cull polygons based on the direction that they face."
//  * SL-SubShaderTags.html: Queue "Transparent: Specifies the Transparent render queue."
//  * SL-Properties.html: _ExampleName("Example color", Color) = (.25, .5, .5, 1) "maps to a float4 in your shader code".
//  * SL-VertexProgramInputs.html: "POSITION is the vertex position", "COLOR is the per-vertex color, typically a float4";
//    its example uses o.pos = UnityObjectToClipPos(v.vertex) and fixed4 frag (v2f i) : SV_Target { return i.color; }.
Shader "UntitledGuitarApp/HudLearn"
{
    Properties
    {
        _Color ("Tint (rgb colour, a brightness)", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One
            BlendOp Max

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

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

            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Brightness is all an additive display can show, so alpha scales the colour.
                return fixed4(i.color.rgb * i.color.a, i.color.a);
            }
            ENDCG
        }
    }
}
