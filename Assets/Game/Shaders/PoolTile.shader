// ============================================================
// Game/PoolTile —— 千禧年泳池瓷砖（程序化，无需贴图）
// 手写 URP HLSL：
//   - 世界坐标生成 15cm 方砖 + 白色勾缝
//   - 按表面法线选投影平面，墙面/地面瓷砖都横平竖直
//   - 每块砖细微色差 + 釉面高光
//   - 水下焦散：画进材质本身（池底/池壁水下部分），由水面折射统一显示，
//     与瓷砖同步扭曲/压缩，不会摩尔纹（_CausticsIntensity = 0 时关闭）
// ============================================================
Shader "Game/PoolTile"
{
    Properties
    {
        _TileColor   ("Tile Color", Color) = (0.29, 0.56, 0.89, 1)  // 水蓝
        _GroutColor  ("Grout Color", Color) = (0.92, 0.93, 0.94, 1) // 白缝
        _TileSize    ("Tile Size (m)", Float) = 0.15                // 每块砖边长
        _GroutWidth  ("Grout Width", Range(0, 0.5)) = 0.08          // 缝宽（砖边长的比例）
        _Smoothness  ("Smoothness", Range(0, 1)) = 0.6              // 釉面光泽
        _CausticsIntensity ("Underwater Caustics", Range(0, 2)) = 0.0
        _CausticsScale     ("Caustics Scale", Float) = 1.2
        _CausticsDistortion("Caustics Distortion", Range(0, 2)) = 0.5
        _CausticsSharpness ("Caustics Sharpness", Range(1, 8)) = 3
        _CausticsSpeed     ("Caustics Speed", Range(0, 3)) = 0.8
        _CausticsColor     ("Caustics Color", Color) = (0.60, 0.85, 1.0, 1)
        _WaterLevel        ("Water Level (y)", Float) = -0.25       // 水面高度，以下才有焦散
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "PoolWaterShared.hlsl"

            // SRP Batcher 要求材质属性放在 CBUFFER 里
            CBUFFER_START(UnityPerMaterial)
                float4 _TileColor;
                float4 _GroutColor;
                float  _TileSize;
                float  _GroutWidth;
                float  _Smoothness;
                float  _CausticsIntensity;
                float  _CausticsScale;
                float  _CausticsDistortion;
                float  _CausticsSharpness;
                float  _CausticsSpeed;
                float4 _CausticsColor;
                float  _WaterLevel;
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

            // 简单 hash：给每块砖一个伪随机数（千禧年瓷砖的烧制色差感）
            float2 Hash2(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = GetWorldSpaceViewDir(IN.positionWS);
                float3 wp = IN.positionWS;

                // 按表面朝向选投影平面：
                //   顶/底面 -> XZ，东/西墙 -> ZY，南/北墙 -> XY
                // 保证垂直墙面上瓷砖也是横平竖直的方砖
                float3 an = abs(N);
                float2 projPos;
                if (an.y >= an.x && an.y >= an.z) projPos = wp.xz; // 顶/底面
                else if (an.x >= an.z)            projPos = wp.zy; // 东/西墙
                else                              projPos = wp.xy; // 南/北墙
                float2 uv = projPos / _TileSize;

                // 砖格 + 勾缝
                float2 cell = floor(uv);
                float2 f    = frac(uv);
                float isGrout = step(1.0 - _GroutWidth, f.x) + step(1.0 - _GroutWidth, f.y);

                // 每块砖色差（±12%，千禧年瓷砖烧制色差感更明显）
                float2 rnd = Hash2(cell);
                float3 tileColor = _TileColor.rgb * lerp(0.88, 1.12, rnd.x);

                float3 albedo = lerp(tileColor, _GroutColor.rgb, saturate(isGrout));

                // ---- 光照 ----
                Light light = GetMainLight();
                float ndl = saturate(dot(N, light.direction));
                half3 diffuse = albedo * (light.color * ndl);

                half3 ambient = SampleSH(N);

                // 简单高光（瓷砖釉面）
                float3 H = normalize(light.direction + V);
                half spec = pow(saturate(dot(N, H)), lerp(64.0, 8.0, _Smoothness)) * _Smoothness * ndl;
                half3 specular = light.color * spec * 0.5;

                half3 color = diffuse + ambient * albedo + specular;

                // ---- 水下焦散：画进材质本身（池底/池壁水下部分），由水面折射统一显示 ----
                // 只有水面（_WaterLevel）以下的部分才有焦散，水上干池壁没有
                if (_CausticsIntensity > 0.0)
                {
                    float underwater = 1.0 - smoothstep(_WaterLevel - 0.15, _WaterLevel, wp.y);
                    float caustic = PoolCausticPattern(projPos, _Time.y * _CausticsSpeed,
                                                       _CausticsScale, _CausticsDistortion,
                                                       _CausticsSharpness);
                    color += _CausticsColor.rgb * caustic * _CausticsIntensity * underwater;
                }

                // 雾（URP）
                float fogFactor = ComputeFogFactor(IN.positionHCS.z);
                color = MixFog(color, fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
