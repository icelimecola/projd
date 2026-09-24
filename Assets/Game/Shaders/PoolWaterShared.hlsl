// ============================================================
// PoolWaterShared.hlsl —— 水面波浪与 caustics 共享的数学（同源保证）
// WaterSurface.shader 和 PoolCaustics.shader 都 include 本文件：
//   - PoolWaterNormal：水面波浪法线（程序化，无贴图）
//   - PoolCausticPattern：波纹干涉 caustics = 多方向平面波相长 + 波浪扭曲
// 两个 shader 用同一个波浪函数，光斑跟着水面波浪同步流动。
// ============================================================
#ifndef POOL_WATER_SHARED_INCLUDED
#define POOL_WATER_SHARED_INCLUDED

// 水面波浪法线（poolcore 微澜版）：
//   - 12 个高频细碎涟漪：方向均匀铺满 360°（无主导方向）、频率交错（避免规律感）、振幅交错
//   - 1 个超低频"呼吸"波：整池水面极缓慢的起伏（果冻感）
// 解析求导得到斜率 -> 法线（零贴图、纯数学）
float3 PoolWaterNormal(float2 pos, float t, float amplitude)
{
    float hx = 0.0;
    float hz = 0.0;

    // 低频呼吸：波长 ~3.5m，速度极慢，让整池水面有缓慢起伏
    {
        float2 dir = float2(1.0, 0.0);
        float k = 1.8;
        float w = 0.35;
        float a = amplitude * 1.6;
        float c = cos(dot(pos, dir) * k + t * w);
        hx += a * k * c * dir.x;
        hz += a * k * c * dir.y;
    }

    // 12 个高频细碎涟漪：方向每 30° 一个（+ 错开角），频率 4~10，速度 0.8~2.45
    for (int i = 0; i < 12; i++)
    {
        float ang = (float)i * 3.14159265 / 6.0 + 0.13;  // 30° 间隔 + 轻微错开
        float2 dir = float2(cos(ang), sin(ang));
        float k = 4.0 + 0.55 * (float)i;                 // 频率交错 -> 波长 1.57m~0.63m（细碎）
        float w = 0.8 + 0.15 * (float)i;                 // 速度交错
        float r = frac(sin((float)i * 12.9898 + 0.5) * 43758.5453); // 伪随机 0~1
        float a = amplitude * 0.07 * (0.4 + 0.6 * r);    // 振幅交错，各向略不均匀更自然
        float c = cos(dot(pos, dir) * k + t * w);
        hx += a * k * c * dir.x;
        hz += a * k * c * dir.y;
    }

    return normalize(float3(-hx, 1.0, -hz));
}

// 波纹干涉 caustics：多方向平面波相加，波峰相长形成交织波纹亮纹网络。
// 这是真实水面 caustics（平行光被波浪折射聚焦）的形态近似。
//   p          —— 三平面投影后的图案坐标（已在各自平面，墙面/天花板都不拉扯）
//   t          —— 时间（与水面波浪同一个 t，保证同步）
//   scale      —— 波纹密度
//   distortion —— 波浪斜率对图案的扭曲量（同源驱动）
//   sharpness  —— 亮纹锐化
float PoolCausticPattern(float2 p, float t, float scale, float distortion, float sharpness)
{
    p *= scale;

    // 用该投影平面上的波浪斜率扰动图案坐标（同空间，墙面/天花板都不拉扯）
    float3 wn = PoolWaterNormal(p, t, 0.03);
    p += wn.xz * distortion;

    // 多方向平面波干涉（8 个不同方向/频率，波峰相长 = 亮纹）
    float c = 0.0;
    c += sin(p.x * 2.00 + t);
    c += sin(p.y * 2.20 + t * 1.10);
    c += sin((p.x + p.y) * 1.40 + t * 0.90);
    c += sin((p.x - p.y) * 1.60 + t * 1.20);
    c += sin(p.x * 3.10 - t * 0.70);
    c += sin(p.y * 3.30 + t * 0.80);
    c += sin((p.x * 1.20 + p.y * 2.70) + t * 1.30);
    c += sin((p.x * 2.50 - p.y * 1.10) + t * 0.60);

    // 归一化到 0-1，再高次幂锐化：只留波峰 -> 亮纹
    c = saturate(c * 0.125 + 0.5);
    return pow(c, sharpness);
}

#endif // POOL_WATER_SHARED_INCLUDED
