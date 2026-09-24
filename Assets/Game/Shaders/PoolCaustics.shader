// ============================================================
// Game/PoolCaustics —— 水上反射水光（天花板 + 四面墙）
// 蜂窝亮斑 + 水面波浪扭曲（与水面同源，PoolWaterShared.hlsl）。
// 三平面投影：按表面法线选投影面，天花板和墙都能正确铺开。
// additive 混合叠加在表面上方一点，半透明、不写深度。
// ============================================================
Shader "Game/PoolCaustics"
{
    Properties
    {
        _Color      ("Caustic Color", Color) = (0.75, 0.92, 1.0, 1)
        _Scale      ("Pattern Scale", Float) = 1.2
        _Speed      ("Wave Speed", Range(0, 3)) = 0.8
        _Distortion ("Wave Distortion", Range(0, 2)) = 0.5
        _Sharpness  ("Sharpness", Range(1, 8)) = 3
        _Intensity  ("Intensity", Range(0, 2)) = 0.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+10" }

        Pass
        {
            // additive：只加亮，不覆盖
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "PoolWaterShared.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Scale;
                float  _Speed;
                float  _Distortion;
                float  _Sharpness;
                float  _Intensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 与水面同一个时间 t（PoolCausticPattern 内部会用同源波浪法线扭曲）
                float t = _Time.y * _Speed;

                // 三平面投影：顶/底面 -> XZ，东/西墙 -> ZY，南/北墙 -> XY
                float3 N = normalize(IN.normalWS);
                float3 an = abs(N);
                float2 p;
                if (an.y >= an.x && an.y >= an.z) p = IN.positionWS.xz; // 天花板
                else if (an.x >= an.z)            p = IN.positionWS.zy; // 东/西墙
                else                              p = IN.positionWS.xy; // 南/北墙

                float c = PoolCausticPattern(p, t, _Scale, _Distortion, _Sharpness);
                float3 col = _Color.rgb * c * _Intensity;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
