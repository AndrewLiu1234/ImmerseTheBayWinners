// UntitledGuitarApp/GhostHand: the look of every ghost hand (the procedural capsule hand and the Blender hand).
// Built-in render pipeline, unlit, ADDITIVE. The glasses add light to the real world, so black is invisible and
// the hand is made of light: a bright fresnel rim that carries the silhouette, a faint fill the real strings show
// through, and fingertips that are the brightest thing in view. No dark outline anywhere.
//
// Load it with Resources.Load<Shader>("Shaders/GhostHand") (Shader.Find does not work for it in a build).
//
// TWO PASSES, SO THE HAND READS AS ONE BODY OF LIGHT
//   An additive surface adds its light wherever it is drawn, so overlapping parts (a finger in front of the palm, the
//   buried start of a finger, two fingers crossing) used to add up into bright seams: "a stack of pills".
//   Pass 1 writes only the depth of the whole mesh (ColorMask 0, ZWrite On). Pass 2 adds the colour with ZTest LEqual and
//   ZWrite Off, so only the front-most surface of the hand passes; everything behind it inside the same mesh fails.
//   Both passes place the vertex with the same UnityObjectToClipPos, so the front-most surface has the same depth in both
//   (and pass 2 is biased one depth unit toward the camera, in case the two programs round that depth differently).
//   * This only joins parts that are drawn by ONE renderer with ONE material (one draw of each pass). The capsule hand is
//     one mesh for that reason; the Blender hand must be one mesh with one material too, or its parts overlap again.
//   * Faded parts (vertex colour "fade out", coverage below _DepthCutoff) write no depth, so a nearly invisible forearm
//     never cuts a hole into the fingers behind it.
//   * Queue Transparent+100: the hand draws after everything in the Transparent queue (neck lines, fingertip dots,
//     labels), so its depth never hides them; they show through the hand. Something that must show through it and
//     uses depth testing must be in a queue below 3100 (or use ZTest Always, as the HUD and the reticle already do).
//
// VERTEX COLOUR CONVENTION (the same for every hand mesh, skinned or not; alpha is ignored):
//   red   minus green = fingertip glow   -> paint the fingertips PURE RED    (1, 0, 0)
//   blue  minus green = fade out         -> paint parts to hide PURE BLUE    (0, 0, 1)   (soft ends of a mesh)
//   any grey (white, black, in between)  = plain hand.
//   So a mesh with no painted colours renders as a plain hand whatever default colour the importer gives it.
//
// PER-VERTEX LOOK DATA (TEXCOORD0, read only when _VertexControl = 1; the capsule hand sets it, any other mesh leaves it 0
// and its own UVs are ignored):
//   x = dim      0 = full brightness .. 1 = off          (finger-by-finger emphasis without one renderer per finger)
//   y = hollow   0 = full fill .. 1 = rim only           (the palm and the wrist carry less fill than the fingers)
//   z = tip off  0 = the fingertip glows .. 1 = no glow  (only the fingers that press a string glow)
//   w = colour slot of the fingertip glow: 0 = _TipColor, 1-4 = _TipColor1 .. _TipColor4 (flag ghost.fingerColors)
//
// PER-RENDERER VALUES (set them with a MaterialPropertyBlock, the material stays shared):
//   _Emphasis  1 = this renderer at full brightness, 0.3 = dimmed
//   _Fade      1 = normal, 0.4 = "fade to 40% after success"
//   _TipColor (and _TipColor1-4 with _VertexControl)  fingertip glow colours
//
// STEREO: nothing here depends on the screen, on a grab pass or on a depth texture. The view direction comes from
// _WorldSpaceCameraPos, which Unity sets for each camera, so two ordinary cameras drawing the left and right halves
// of one display each get their own correct rim. The depth prepass uses the camera's own depth buffer (each eye
// camera clears it), which every camera and every RenderTexture with a depth buffer has.
//
// SKINNED MESHES: only UnityObjectToClipPos / unity_ObjectToWorld / UnityObjectToWorldNormal touch the vertex, so the
// shader works unchanged on a SkinnedMeshRenderer (Unity skins the vertices before this vertex program runs).
//
// SOURCES (Unity 2021.3 manual, local copy under Unity.app/Contents/Documentation/en/Manual, online at
// https://docs.unity3d.com/2021.3/Documentation/Manual/<page>.html), and the include files that ship with
// Unity 2021.3.3f1 under Unity.app/Contents/CGIncludes:
//   SL-Properties              property syntax: Color, Range(min, max); "Color ... This maps to a float4 in your shader code"
//   SL-PropertiesInPrograms    "Color and Vector properties map to float4, half4 or fixed4", "Range and Float properties
//                              map to float, half or fixed"; "When using Linear color space, all material color
//                              properties are supplied as sRGB colors, but are converted into linear values"
//   SL-SubShaderTags           "Queue" = "[queue name] + [offset]": "Use an unnamed queue, at a given offset from the named
//                              queue" (example "Geometry+1"); "RenderType", "IgnoreProjector" = "True"
//   SL-Pass, shader-objects    a SubShader may hold several Passes; "For every Pass that it renders: ... Unity sets the
//                              render state as defined in the Pass"
//   SL-PassTags                "In the Built-in Render Pipeline, if you do not set a LightMode tag, Unity renders the Pass
//                              without any lighting or shadows" (neither pass sets one, so both are rendered)
//   SL-ColorMask               "Sets the color channel writing mask, which prevents the GPU from writing to channels in the
//                              render target"; "disable color writes completely so that you can populate one buffer with
//                              data without writing to others". NOTE: the 2021.3 page's table row for 0 reads "Enables color
//                              writes to the R, G, B, and A channels", which contradicts that text; the current manual
//                              (docs.unity.com, engine 6000.0, ShaderLab command ColorMask) reads "0 | Disables color writes
//                              to the R, G, B, and A channels".
//   SL-CullAndDepth (2018.4 manual, https://docs.unity3d.com/2018.4/Documentation/Manual/SL-CullAndDepth.html), example
//                              "Transparent shader with depth writes": "Usually semitransparent shaders do not write into the
//                              depth buffer. However, this can create draw order problems, especially with complex non-convex
//                              meshes." Its first pass: "// extra pass that renders to depth buffer only  Pass { ZWrite On
//                              ColorMask 0 }", then the transparent pass. (The 2021.3 manual has no such example and does not
//                              spell out the pass order; this two-pass order is the one that example relies on.)
//   SL-ZWrite                  "On: Enables writing to the depth buffer." "Off: Disables writing to the depth buffer."
//   SL-ZTest                   "LEqual: Draw geometry that is in front of or at the same distance as existing geometry. Do
//                              not draw geometry that is behind existing geometry. This is the default value."
//   SL-Offset                  "Offset <factor>, <units>"; units: "Scales the minimum resolvable depth buffer value, to produce
//                              a constant depth offset ... A negative value means that the GPU draws the polygon closer to the
//                              camera" (range -1 through 1); "force the GPU to draw geometry on top of other geometry that is
//                              at the same depth. This can help you to avoid unwanted visual effects such as z-fighting"
//   SL-Blend                   "Blend One One // Additive"
//   SL-Cull                    "Cull Back ... This is the default value"
//   SL-ShaderSemantics         "clip HLSL instruction stops rendering a pixel if value is negative"; "TEXCOORD0, TEXCOORD1
//                              etc are used to indicate arbitrary high precision data"; up to 8 interpolators on the oldest
//                              targets (this shader uses at most 4)
//   SL-VertexProgramInputs     POSITION / NORMAL / COLOR inputs; "TEXCOORD0 is the first UV coordinate, typically float2,
//                              float3 or float4"; "When the mesh data contains fewer components than are needed by the vertex
//                              shader input, the rest are filled with zeroes, except for the .w component which defaults to 1"
//   SL-VertexFragmentShaderExamples   "Unlit/SkyReflection Per Pixel": worldPos + worldNormal interpolators and
//                              normalize(UnityWorldSpaceViewDir(i.worldPos)) in the fragment program
//   ScriptReference Mesh.SetUVs  "A channel value of 0 corresponds to the channel that is commonly called "UV0", and maps
//                              to the shader semantic TEXCOORD0" (how the capsule hand fills the look data)
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
        _TipColor1 ("Fingertip glow colour, finger 1 (capsule hand)", Color) = (0.86, 0.96, 1.0, 1)
        _TipColor2 ("Fingertip glow colour, finger 2 (capsule hand)", Color) = (0.86, 0.96, 1.0, 1)
        _TipColor3 ("Fingertip glow colour, finger 3 (capsule hand)", Color) = (0.86, 0.96, 1.0, 1)
        _TipColor4 ("Fingertip glow colour, finger 4 (capsule hand)", Color) = (0.86, 0.96, 1.0, 1)
        _Fill ("Fill strength (0.15 - 0.25)", Range(0.0, 1.0)) = 0.2
        _RimStrength ("Rim strength", Range(0.0, 4.0)) = 1.0
        _RimPower ("Rim sharpness (higher = thinner)", Range(0.5, 8.0)) = 3.0
        _TipGlow ("Fingertip glow strength", Range(0.0, 4.0)) = 0.9
        _Brightness ("Overall brightness (tune to the glasses' dimming)", Range(0.0, 4.0)) = 1.0
        _Emphasis ("Emphasis (per renderer)", Range(0.0, 2.0)) = 1.0
        _Fade ("Fade (per renderer)", Range(0.0, 1.0)) = 1.0
        _DepthCutoff ("Coverage below which a faded part hides nothing behind it", Range(0.0, 1.0)) = 0.5
        _VertexControl ("Per-vertex look data in TEXCOORD0 (1 = capsule hand, 0 = any other mesh)", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        // ---- Pass 1: depth only. The depth of the hand's front-most surface, no colour. ----
        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Back

            CGPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #include "UnityCG.cginc"

            struct appdataDepth
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2fDepth
            {
                float4 pos : SV_POSITION;
                float cover : TEXCOORD0;
            };

            float _DepthCutoff;

            v2fDepth vertDepth (appdataDepth v)
            {
                v2fDepth o;
                o.pos = UnityObjectToClipPos(v.vertex);            // the same placement as the colour pass
                o.cover = 1.0 - saturate(v.color.b - v.color.g);   // the vertex colour convention above
                return o;
            }

            fixed4 fragDepth (v2fDepth i) : SV_Target
            {
                clip(i.cover - _DepthCutoff);                      // faded parts write no depth
                return 0;                                          // ColorMask 0: nothing reaches the colour buffer
            }
            ENDCG
        }

        // ---- Pass 2: colour, added only where this surface is the front-most one of the hand. ----
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Offset 0, -1        // one depth unit toward the camera: the front-most surface passes even if the two
                                // programs round its depth differently (no slope term, so nothing behind it gets through)
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
                float4 control : TEXCOORD0;     // the per-vertex look data (capsule hand only, see above)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float4 control : TEXCOORD2;
                fixed4 color : COLOR;
            };

            fixed4 _Color;
            fixed4 _RimColor;
            fixed4 _TipColor;
            fixed4 _TipColor1;
            fixed4 _TipColor2;
            fixed4 _TipColor3;
            fixed4 _TipColor4;
            float _Fill;
            float _RimStrength;
            float _RimPower;
            float _TipGlow;
            float _Brightness;
            float _Emphasis;
            float _Fade;
            float _VertexControl;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                o.control = v.control * _VertexControl;     // any other mesh: its UVs are ignored, the data reads as 0
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

                // per-vertex look data (all 0 unless _VertexControl is 1)
                float dim = saturate(i.control.x);
                float hollow = saturate(i.control.y);
                float tipOn = 1.0 - saturate(i.control.z);
                float slot = i.control.w;

                // the fingertip colour of this vertex's slot: exactly one weight is 1 for slots 1-4, none for slot 0
                float3 tipColor = _TipColor.rgb;
                tipColor = lerp(tipColor, _TipColor1.rgb, saturate(1.0 - abs(slot - 1.0)));
                tipColor = lerp(tipColor, _TipColor2.rgb, saturate(1.0 - abs(slot - 2.0)));
                tipColor = lerp(tipColor, _TipColor3.rgb, saturate(1.0 - abs(slot - 3.0)));
                tipColor = lerp(tipColor, _TipColor4.rgb, saturate(1.0 - abs(slot - 4.0)));

                // The strengths below are meant as fractions of what the eye sees. In a Linear colour space project Unity
                // hands the colours over in linear light, so bring them back to display values, mix, and convert the result.
                float3 fillColor = _Color.rgb;
                float3 rimColor = _RimColor.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                fillColor = LinearToGammaSpace(fillColor);
                rimColor = LinearToGammaSpace(rimColor);
                tipColor = LinearToGammaSpace(tipColor);
                #endif

                float3 c = fillColor * (_Fill * (1.0 - hollow)) + rimColor * (rim * _RimStrength) + tipColor * (tip * tipOn * _TipGlow);
                c = c * (cover * (1.0 - dim) * _Emphasis * _Fade * _Brightness);

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
