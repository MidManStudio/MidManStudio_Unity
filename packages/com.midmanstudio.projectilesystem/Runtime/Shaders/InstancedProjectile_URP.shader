// InstancedProjectile_URP.shader
// FIX: added explicit #ifdef UNITY_INSTANCING_ENABLED branches in vert.
//
// Without the ifdef, UNITY_ACCESS_INSTANCED_PROP(_UVRect) in the non-instanced
// (combined-mesh DrawMesh) path returns the MATERIAL's default _UVRect value.
// The supplied MidMan_InstancedProjectile_URP.mat had _UVRect = (0.24, 0, 4.08, 1.2)
// which corrupted every UV in the combined-mesh batch. The fix makes the non-instanced
// path pass UVs straight through (they are pre-baked by ProjectileRenderer2D) and
// uses vertex color directly for the fade alpha.

Shader "MidMan/InstancedProjectile_URP"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D)                    = "white" {}
        _UVRect  ("UV Rect (xy = offset, zw = scale)", Vector) = (0, 0, 1, 1)
        _Color   ("Tint Color", Color)                   = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            Name "Unlit"

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 col        : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

#ifdef UNITY_INSTANCING_ENABLED
                // ── Instanced path (DrawMeshInstanced) ─────────────────────────
                // Per-instance _UVRect remaps the quad's [0,1] UV into the atlas rect.
                // Per-instance _Color carries the lifetime-fade alpha from the MPB.
                // Vertex color on the procedural quad is white, so multiplying is a no-op.
                float4 rect           = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                output.uv             = input.uv * rect.zw + rect.xy;
                float4 instanceColor  = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                output.col            = instanceColor * input.color;
#else
                // ── Combined-mesh path (DrawMesh) ───────────────────────────────
                // UVs are pre-baked per-vertex by ProjectileRenderer2D — pass straight through.
                // Fade alpha is baked into vertex color by ProjectileRenderer2D — use directly.
                // _Color and _UVRect material defaults are intentionally NOT read here so that
                // a misconfigured material default cannot corrupt either UVs or alpha.
                output.uv  = input.uv;
                output.col = input.color;
#endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 texCol  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                texCol.rgb   *= input.col.rgb;
                texCol.a     *= input.col.a;
                return texCol;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
