// InstancedProjectile.shader
// Required for ProjectileRenderer2D.cs — DrawMeshInstanced passes _UVRect
// and _Color as per-instance arrays via MaterialPropertyBlock.SetVectorArray.
//
// Built-in Render Pipeline.
// For URP use InstancedProjectile_URP.shader instead.
//
// FIX: Added float4 color : COLOR to appdata so the combined-mesh (DrawMesh)
// fallback path can carry per-vertex fade-alpha baked by ProjectileRenderer2D.
// The instance _Color is multiplied by vertex color, so:
//   - Instanced path:   vertex color = (1,1,1,1) white → _Color drives fade
//   - Combined-mesh:    vertex color carries fade → _Color default (1,1,1,1) is a no-op

Shader "MidMan/InstancedProjectile"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
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
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            sampler2D _MainTex;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float4 color  : COLOR;          // FIX: needed for combined-mesh vertex alpha
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.pos = UnityObjectToClipPos(v.vertex);

                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                o.uv = v.uv * rect.zw + rect.xy;

                // FIX: multiply by vertex color so combined-mesh fade-alpha works.
                // In the instanced path the quad vertices have white vertex color so
                // this multiplication is a no-op — _Color alone controls the tint/fade.
                o.col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color) * v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 texCol = tex2D(_MainTex, i.uv);
                texCol.a   *= i.col.a;
                texCol.rgb *= i.col.rgb;
                return texCol;
            }
            ENDCG
        }
    }

    Fallback Off
}
