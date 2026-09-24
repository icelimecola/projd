// ============================================================
// Game/WaterSurface —— 泳池水面（M5 版：深度变色 + 水线）
// 手写 URP HLSL：
//   - 折射：扭曲屏幕 UV 采样 _CameraOpaqueTexture（池底/池壁瓷砖在水下晃动）
//   - 深度变色：采样 _CameraDepthTexture，水深越深水色越蓝暗，浅水透亮
//   - 水线：水深≈0 的池壁接触处浮出浅色水线
//   - 菲涅尔：正视看折射，斜视反射天色；程序化波法线 + 波光高光
// 前提：URP Asset 需开启 Opaque Texture 和 Depth Texture（生成器会自动开启）。
// ============================================================
Shader "Game/WaterSurface"
{
    Properties
    {
        _WaterColor        ("Water Tint", Color) = (0.15, 0.52, 0.60, 0.55)
        _DeepColor         ("Deep Water Color", Color) = (0.02, 0.16, 0.40, 1)
        _DepthScale        ("Depth Scale (m)", Float) = 1.5
        _ReflectionColor   ("Reflection Color", Color) = (0.80, 0.88, 0.98, 1)
        _WaveStrength      ("Wave Strength", Range(0, 0.2)) = 0.03
        _WaveSpeed         ("Wave Speed", Range(0, 5)) = 0.8
        _RefractStrength   ("Refraction Strength", Range(0, 0.1)) = 0.012
        _FresnelPower      ("Fresnel Power", Range(1, 16)) = 3.5
        _Gloss             ("Gloss", Range(8, 2048)) = 512
        _SpecStrength      ("Specular Strength", Range(0, 5)) = 0.8
        _Smoothness        ("Reflection Smoothness", Range(0, 1)) = 0.97
        _ReflectionWave    ("Reflection Wave Influence", Range(0, 1)) = 0.05
        _WaterlineColor    ("Waterline Color", Color) = (0.70, 0.90, 1.0, 1)
        _WaterlineWidth    ("Waterline Width (m)", Range(0, 0.5)) = 0.15
        _WaterlineStrength ("Waterline Strength", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            // 水面按不透明渲染（折射画面本身就是"实心水"的观感）
            // 保持 Transparent 队列，保证在 Opaque/Depth Texture 捕获之后采样

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "PoolWaterShared.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _WaterColor;
                float4 _DeepColor;
                float  _DepthScale;
                float4 _ReflectionColor;
                float  _WaveStrength;
                float  _WaveSpeed;
                float  _RefractStrength;
                float  _FresnelPower;
                float  _Gloss;
                float  _SpecStrength;
                float  _Smoothness;
                float  _ReflectionWave;
                float4 _WaterlineColor;
                float  _WaterlineWidth;
                float  _WaterlineStrength;
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

            // 水面波浪法线在 PoolWaterShared.hlsl 里（与 caustics 同源共享）

            half4 frag(Varyings IN) : SV_Target
            {
                float t = _Time.y * _WaveSpeed;
                float3 N = PoolWaterNormal(IN.positionWS.xz, t, _WaveStrength);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // ---- 折射：扭曲屏幕 UV，采样场景不透明颜色（池底/池壁瓷砖） ----
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS.xy);
                float2 refractUV = screenUV + float2(N.x * _RefractStrength * (_ScreenParams.y / _ScreenParams.x),
                                                     N.z * _RefractStrength);
                float3 refracted = SampleSceneColor(refractUV);

                // ---- 水下深度：水面到池底/池壁的视线距离 ----
                // 深度纹理只含不透明物体（水面是透明队列，不会写进去），所以 sceneDepth 是池底/池壁的深度
                float rawDepth = SampleSceneDepth(refractUV);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float3 positionVS = TransformWorldToView(IN.positionWS);
                float waterEyeDepth = -positionVS.z;
                float waterDepth = max(0.0, sceneEyeDepth - waterEyeDepth);
                float depth01 = saturate(waterDepth / _DepthScale);

                // ---- 菲涅尔：正视看折射，斜视看反射 ----
                float fres = pow(1.0 - saturate(dot(N, V)), _FresnelPower);

                // ---- 反射：镜面倒影（几乎用几何法线，倒影清晰不被波浪搅糊） ----
                // _ReflectionWave 控制波浪对反射的扰动（0 = 完全镜面，0.05 = 极轻微涟漪）
                float3 smoothN = normalize(lerp(float3(0.0, 1.0, 0.0), N, _ReflectionWave));
                float3 reflectVec = reflect(-V, smoothN);
                float3 envReflection = GlossyEnvironmentReflection(reflectVec, 1.0 - _Smoothness, 1.0);
                float hasEnv = dot(envReflection, envReflection) > 0.0001 ? 1.0 : 0.0;
                float3 reflection = lerp(_ReflectionColor.rgb, envReflection, hasEnv);

                float3 col = lerp(refracted, reflection, fres);

                // ---- 深度变色：浅水透亮、深水蓝暗 ----
                // 水色 tint 只作用于折射部分（水下），反射部分保持环境色，避免整片发白
                float3 waterTint = lerp(_WaterColor.rgb, _DeepColor.rgb, depth01);
                float tintAmount = _WaterColor.a * (0.35 + depth01 * 0.65);
                col = lerp(col, waterTint, tintAmount * (1.0 - fres));

                // ---- 水线：水深≈0 的池壁接触处浮出浅色水线 ----
                // 注意：变量不能叫 line（HLSL 保留字，会编译报错导致紫色）
                float waterline = 1.0 - smoothstep(0.0, _WaterlineWidth, waterDepth);
                col = lerp(col, _WaterlineColor.rgb, waterline * _WaterlineStrength * (1.0 - fres));

                // 水下焦散已移入 PoolTile 材质（池底/池壁自带，随折射统一显示，避免摩尔纹）

                // ---- 波光粼粼：主光高光 ----
                Light light = GetMainLight();
                float3 H = normalize(light.direction + V);
                float ndl = saturate(dot(N, light.direction));
                float spec = pow(saturate(dot(N, H)), _Gloss) * ndl;
                col += light.color * spec * _SpecStrength;

                // 雾（URP）
                float fogFactor = ComputeFogFactor(IN.positionHCS.z);
                col = MixFog(col, fogFactor);

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
