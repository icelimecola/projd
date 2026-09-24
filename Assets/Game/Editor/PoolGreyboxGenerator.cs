using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 千禧年泳池灰盒生成器（Editor 工具）。
/// 用法：Unity 菜单栏 → Tools → 泳池灰盒 → 生成
/// 功能：一键生成 房间 + 池坑 + 池边平台 +（如缺失）玩家。
/// 可重复运行：再次生成会先删除旧的灰盒；想调整比例，改下面常量即可。
/// </summary>
public static class PoolGreyboxGenerator
{
    // ================= 尺寸参数（想调整比例，改这里） =================
    private const float RoomWidth     = 20f;   // 房间宽度  X
    private const float RoomDepth     = 12f;   // 房间深度  Z
    private const float RoomHeight    = 5f;    // 房间高度  Y
    private const float PoolWidth     = 8f;    // 池坑宽度  X
    private const float PoolDepth     = 4f;    // 池坑深度  Z
    private const float PoolDepthY    = 1.5f;  // 池坑挖深  Y（向下）
    private const float PlatformWidth = 0.5f;  // 预留：池沿装饰宽度（当前地板直接铺到池壁内沿，池边自然形成平台）
    private const float WallThickness = 0.2f;  // 墙 / 地板厚度
    // 消除 z-fighting：与地板顶面共面的池壁顶面抬高一点点，让深度不再相同（视觉上更像凸起的池沿）
    private const float ZFightOffset  = 0.002f;
    private const string RootName     = "PoolGreybox";
    private const string MaterialDir  = "Assets/Game/Materials";

    // ================= 配色（千禧年泳池：白墙白地 + 浅青绿瓷砖） =================
    private static readonly Color FloorColor       = new Color(0.93f, 0.93f, 0.93f); // 地板白
    private static readonly Color WallColor        = new Color(0.93f, 0.93f, 0.93f); // 房间墙白
    private static readonly Color CeilingColor     = new Color(0.95f, 0.95f, 0.95f); // 天花板白
    private static readonly Color PoolWallTile     = new Color(0.631f, 0.690f, 0.588f); // 池壁浅青绿（用户指定 161,176,150）
    private static readonly Color PoolBottomTile   = new Color(0.51f, 0.58f, 0.49f);   // 池底同色系稍深

    /// <summary>
    /// 一键新建干净的泳池场景：空场景 + 灯光氛围 + 泳池灰盒 + Player + 主相机。
    /// 原场景（如 projd-01）不会被改动。
    /// </summary>
    [MenuItem("Tools/泳池灰盒/新建泳池场景")]
    public static void CreateNewPoolScene()
    {
        // 1. 新建空场景（若当前场景有未保存修改，Unity 会先询问是否保存）
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 2. 生成泳池灰盒 + Player + 主相机 + 灯光氛围（Generate 内部会配置灯光与后处理）
        Generate();

        // 3. 场景文件不存在时自动保存为 Pool-01，方便直接使用
        string path = "Assets/Game/Scenes/Pool-01.unity";
        if (!System.IO.File.Exists(path))
        {
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
            Debug.Log("[泳池灰盒] 场景已保存到 " + path);
        }
        else
        {
            Debug.Log("[泳池灰盒] " + path + " 已存在，请手动 Cmd/Ctrl+S 保存新场景（或另存为其他名字）");
        }
    }

    /// <summary>单独配置灯光与后处理氛围（不重建几何），可反复运行。</summary>
    [MenuItem("Tools/泳池灰盒/配置灯光氛围")]
    public static void ConfigureMood()
    {
        // 1. 主方向光（天窗感）：冷白、从上方倾斜、Soft 阴影
        Light dirLight = FindDirectionalLight();
        if (dirLight == null)
        {
            GameObject lightGo = new GameObject("Directional Light");
            dirLight = lightGo.AddComponent<Light>();
            dirLight.type = LightType.Directional;
            Undo.RegisterCreatedObjectUndo(lightGo, "创建 Directional Light");
        }
        dirLight.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
        dirLight.color = new Color(0.92f, 0.96f, 1.00f); // 冷白
        dirLight.intensity = 1.2f;
        dirLight.shadows = LightShadows.Soft;

        // 2. 环境光：室内无天空盒，用冷白 Flat 环境色给灰盒一个底光
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.72f, 0.80f, 0.92f);

        // 3. 后处理 Global Volume（泛光 + ACES + 轻色调）
        EnsureGlobalVolume();

        // 4. 反射探针：让水面真实映出天花板与灯光
        EnsureReflectionProbe();

        // 5. 氛围收尾：轻雾 + 泳池馆回声
        EnsureAtmosphere();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[泳池灰盒] 灯光氛围已配置（冷白天光 + 泛光 + ACES + 反射探针 + 轻雾/回声）。保存场景后生效。");
    }

    /// <summary>
    /// 氛围收尾：泳池馆长混响（AudioReverbZone，无需素材）。
    /// 雾先关闭：之前的线性雾（10~35m）对 20m 房间太重，导致瓷砖/水面整体发灰白；
    /// 若确认需要空气感，再调轻（如 15~80m）开回。
    /// </summary>
    private static void EnsureAtmosphere()
    {
        // 雾：关闭（怀疑是整体灰白的元凶）
        RenderSettings.fog = false;

        // 泳池馆回声：长混响、开阔空间感
        AudioReverbZone reverb = Object.FindFirstObjectByType<AudioReverbZone>();
        if (reverb == null)
        {
            GameObject go = new GameObject("Pool Reverb Zone");
            reverb = go.AddComponent<AudioReverbZone>();
            Undo.RegisterCreatedObjectUndo(go, "创建 Reverb Zone");
        }
        reverb.room = -400;          // 注意：room/roomHF/reflections/reverb 是 int 类型
        reverb.roomHF = -200;
        reverb.decayTime = 2.8f;     // 长混响（泳池馆回声）
        reverb.decayHFRatio = 0.6f;
        reverb.reflections = -600;
        reverb.reverb = 300;
        reverb.reverbDelay = 0.02f;
        reverb.transform.position = new Vector3(0f, RoomHeight * 0.5f, 0f);
        reverb.minDistance = 1f;
        reverb.maxDistance = 20f;

        // 水声环境底噪（程序化生成，以后可替换为真实素材）
        EnsureAmbienceSource();
    }

    /// <summary>
    /// 创建/复用环境水声 AudioSource：3D 源放在房间中央（这样才会经过 AudioReverbZone 获得泳池馆混响），
    /// 循环播放程序化生成的柔和水流/微澜底噪。
    /// </summary>
    private static void EnsureAmbienceSource()
    {
        AudioSource src = Object.FindFirstObjectByType<AudioSource>();
        if (src == null)
        {
            GameObject go = new GameObject("Pool Ambience");
            src = go.AddComponent<AudioSource>();
            Undo.RegisterCreatedObjectUndo(go, "创建 Ambience AudioSource");
        }

        src.clip = GetOrCreateAmbienceClip();
        src.loop = true;
        src.playOnAwake = true;
        src.volume = 0.5f;       // 环境底噪音量
        src.spatialBlend = 1f;   // 3D：才会被 AudioReverbZone 混响
        src.reverbZoneMix = 1f;  // 完全经过混响
        src.transform.position = new Vector3(0f, 1.5f, 0f); // 房间中央（池子上方）
    }

    /// <summary>取（或生成）程序化水声素材：柔和的白噪声 + 缓慢起伏，保存为 wav 供以后替换。</summary>
    private static AudioClip GetOrCreateAmbienceClip()
    {
        const string path = "Assets/Game/Audio/PoolAmbience.wav";
        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (clip == null)
        {
            GenerateWavFile(path);
            AssetDatabase.ImportAsset(path);
            clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            Debug.Log("[泳池灰盒] 生成程序化水声素材 " + path);
        }
        return clip;
    }

    /// <summary>生成一个 4 秒的柔和水流底噪 wav（PCM16 单声道 44.1kHz）。</summary>
    private static void GenerateWavFile(string path)
    {
        const int sampleRate = 44100;
        const int duration = 4;
        int sampleCount = sampleRate * duration;
        short[] pcm = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // 宽带噪声（水流沙沙声的主体）
            float noise = 2f * (Mathf.PerlinNoise(i * 0.11f, 0.5f) - 0.5f);
            // 高频细节（细碎水声）
            float detail = 2f * (Mathf.PerlinNoise(i * 0.04f, 3.7f) - 0.5f);
            // 低频缓慢起伏（微澜感，~0.13Hz）
            float mod = 0.65f + 0.35f * Mathf.Sin(2f * Mathf.PI * 0.13f * t + 0.7f);
            float s = (noise * 0.30f + detail * 0.20f) * mod;
            s = Mathf.Clamp(s * 0.25f, -1f, 1f); // 压低音量（环境底噪）
            pcm[i] = (short)(s * 32767f);
        }

        // 写 WAV（RIFF + PCM16 单声道）
        using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + sampleCount * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);          // PCM
            writer.Write((short)1);          // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);    // byte rate
            writer.Write((short)2);          // block align
            writer.Write((short)16);         // bits per sample
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(sampleCount * 2);
            foreach (short sample in pcm)
            {
                writer.Write(sample);
            }
        }
    }

    /// <summary>
    /// 创建/复用反射探针（水面反射 + 金属反射用）：房间中心、Baked 模式。
    /// 烘焙链路（三重保险）：
    ///   1. delayCall 延迟到下一帧再 RenderProbe（Generate 刚建完场景，立即烘焙可能抓到未就绪状态）
    ///   2. 轮询等 cubemap 就绪后保存
    ///   3. 同时设为 RenderSettings.customReflectionTexture（全局环境反射兜底：
    ///      即使探针影响范围判定失败，物体也能反射到环境）
    /// </summary>
    private static void EnsureReflectionProbe()
    {
        ReflectionProbe probe = Object.FindFirstObjectByType<ReflectionProbe>();
        if (probe == null)
        {
            GameObject go = new GameObject("Reflection Probe");
            probe = go.AddComponent<ReflectionProbe>();
            Undo.RegisterCreatedObjectUndo(go, "创建 Reflection Probe");
        }

        probe.mode = ReflectionProbeMode.Baked;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
        probe.intensity = 1f;
        probe.boxProjection = true;
        probe.resolution = 512; // 提高倒影清晰度（默认 128 放大后糊）
        probe.size = new Vector3(RoomWidth, RoomHeight, RoomDepth);
        probe.transform.position = new Vector3(0f, RoomHeight * 0.5f, 0f);
        EditorUtility.SetDirty(probe);

        // 延迟到下一帧烘焙（场景对象刚创建完）
        EditorApplication.delayCall += BakeReflectionProbeAndApply;
    }

    private static void BakeReflectionProbeAndApply()
    {
        ReflectionProbe probe = Object.FindFirstObjectByType<ReflectionProbe>();
        if (probe == null)
        {
            return;
        }

        // 官方烘焙 API（Lighting 窗口 Generate Lighting 同款，支持 URP；
        // 之前的 RenderProbe 偏运行时，对 Baked 探针经常烘焙失败导致 cubemap 全黑）
        // Unity 6000 的签名需要 path 参数（烘焙结果的 cubemap 保存路径）
        UnityEditor.Lightmapping.BakeReflectionProbe(probe, "Assets/Game/Materials/PoolReflection.cubemap");

        // 轮询等待烘焙结果（bakedTexture）；超时则删除探针 + 程序化环境 cubemap 兜底
        probeWaitFrames = 0;
        EditorApplication.update += WaitReflectionProbeReady;
    }

    private static int probeWaitFrames;

    private static void WaitReflectionProbeReady()
    {
        ReflectionProbe probe = Object.FindFirstObjectByType<ReflectionProbe>();
        probeWaitFrames++;

        if (probe == null || probeWaitFrames > 120)
        {
            EditorApplication.update -= WaitReflectionProbeReady;
            if (probe == null)
            {
                return;
            }

            // 烘焙失败：删掉探针（customReflection 只在无探针时生效），用程序化环境 cubemap 兜底
            Debug.LogWarning("[泳池灰盒] 反射探针烘焙超时，改用程序化环境反射兜底");
            Object.DestroyImmediate(probe.gameObject);
            ApplyFallbackEnvironment();
            return;
        }

        if (probe.bakedTexture != null && probe.bakedTexture.width > 0)
        {
            EditorApplication.update -= WaitReflectionProbeReady;
            EditorUtility.SetDirty(probe);

            // 成功：设为全局环境反射（探针 + custom 双保险）
            RenderSettings.customReflectionTexture = probe.bakedTexture;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[泳池灰盒] 反射探针烘焙成功（" + probe.bakedTexture.width + "px），已设为全局环境反射。记得保存场景。");
        }
    }

    /// <summary>
    /// 终极兜底：程序化生成环境 cubemap（上=天花板白、下=地板白、侧=池壁青绿），
    /// 设为全局环境反射——保证金属/水面至少反射出正确的环境颜色方向。
    /// </summary>
    private static void ApplyFallbackEnvironment()
    {
        const string path = "Assets/Game/Materials/PoolEnvFallback.cubemap";
        Cubemap cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
        if (cubemap == null)
        {
            cubemap = new Cubemap(32, TextureFormat.RGB24, false);

            Color side = new Color(0.63f, 0.69f, 0.59f); // 池壁青绿
            Color top = new Color(0.95f, 0.95f, 0.95f);  // 天花板白
            Color bottom = new Color(0.93f, 0.93f, 0.93f); // 地板白
            Color[] fill = new Color[32 * 32];
            for (int i = 0; i < fill.Length; i++)
            {
                fill[i] = side;
            }
            cubemap.SetPixels(fill, CubemapFace.PositiveX);
            cubemap.SetPixels(fill, CubemapFace.NegativeX);
            cubemap.SetPixels(fill, CubemapFace.PositiveZ);
            cubemap.SetPixels(fill, CubemapFace.NegativeZ);
            for (int i = 0; i < fill.Length; i++)
            {
                fill[i] = top;
            }
            cubemap.SetPixels(fill, CubemapFace.PositiveY);
            for (int i = 0; i < fill.Length; i++)
            {
                fill[i] = bottom;
            }
            cubemap.SetPixels(fill, CubemapFace.NegativeY);
            cubemap.Apply();

            AssetDatabase.CreateAsset(cubemap, path);
            Debug.Log("[泳池灰盒] 已生成程序化环境 cubemap " + path);
        }

        RenderSettings.customReflectionTexture = cubemap;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[泳池灰盒] 已应用程序化环境反射（金属会映出池壁青绿/天花板白的颜色方向）");
    }

    [MenuItem("Tools/泳池灰盒/生成")]
    public static void Generate()
    {
        // 0. 确保 URP 开启水面需要的缓冲（Opaque Texture 折射 + Depth Texture 深度变色）
        EnsureURPTextures();

        // 1. 清理旧的灰盒（保证可重复运行）
        GameObject old = GameObject.Find(RootName);
        if (old != null)
        {
            Object.DestroyImmediate(old);
        }

        GameObject root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "生成泳池灰盒");

        Transform room = CreateChild(root.transform, "Room");
        Transform pool = CreateChild(root.transform, "Pool");

        // 2. 地板：4 块拼接，中间给池坑留出镂空
        BuildFloor(room);

        // 3. 池壁 + 池底（瓷砖）
        BuildPool(pool);

        // 4. 水面（程序化网格 + 水面 shader）
        BuildWater(pool);

        // 5. 房间四壁 + 天花板
        BuildWallsAndCeiling(room);

        // 6. 天花板水光（水面波纹反光）
        BuildCaustics(room);

        // 6.5 场景道具：灯管阵列 + 池沿 + 爬梯
        BuildProps(room);

        // 7. 玩家（已有则复用）
        EnsurePlayer();

        // 8. 灯光氛围 + 反射探针（明亮冷白白天：天窗方向光 + 冷白环境 + 泛光后处理）
        ConfigureMood();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = root;
        Debug.Log("[泳池灰盒] 生成完成！Hierarchy 里查看 PoolGreybox。记得保存场景（Ctrl/Cmd+S）。");
    }

    // ================= 地板：池坑四周留 0.5m 平台 =================
    private static void BuildFloor(Transform parent)
    {
        Material floor = GetOrCreateMaterial("M_Floor_Grey", FloorColor);

        float halfW = RoomWidth * 0.5f;               // 10
        float halfD = RoomDepth * 0.5f;               // 6
        // 地板内边缘向池壁盒子内收 0.05m：地板厚度侧面藏进池壁实体里，
        // 避免与池壁内面共面（z-fighting），同时池壁顶面自然露出 0.05m 宽的池沿
        const float inset = 0.05f;
        float poolHalfW = PoolWidth * 0.5f + inset;   // 4.05
        float poolHalfD = PoolDepth * 0.5f + inset;   // 2.05
        float t = WallThickness;

        // 地板顶面在 y = 0，厚度向下
        float y = -t * 0.5f;

        // 左右两条：池坑两侧（内边缘收进池壁盒子）
        CreateBox(parent, "Floor_Left",
            new Vector3(-(halfW + poolHalfW) * 0.5f, y, 0f),
            new Vector3(halfW - poolHalfW, t, RoomDepth), floor);
        CreateBox(parent, "Floor_Right",
            new Vector3((halfW + poolHalfW) * 0.5f, y, 0f),
            new Vector3(halfW - poolHalfW, t, RoomDepth), floor);

        // 上下两条：池坑两端（宽度同样含内收量，四块地板内边缘恰好相接，不留缝）
        CreateBox(parent, "Floor_Top",
            new Vector3(0f, y, (halfD + poolHalfD) * 0.5f),
            new Vector3(PoolWidth + inset * 2f, t, halfD - poolHalfD), floor);
        CreateBox(parent, "Floor_Bottom",
            new Vector3(0f, y, -(halfD + poolHalfD) * 0.5f),
            new Vector3(PoolWidth + inset * 2f, t, halfD - poolHalfD), floor);
    }

    // ================= 池坑：四壁 + 池底（程序化瓷砖材质） =================
    private static void BuildPool(Transform parent)
    {
        // 用 Game/PoolTile shader 的程序化瓷砖材质（水蓝砖 + 白缝），无需贴图
        Material wall = GetOrCreateShaderMaterial("M_PoolWall_Tile", "Game/PoolTile");
        ConfigureTile(wall, PoolWallTile, 0.3f);    // 池壁：水下部分焦散（弱于池底但可见）
        Material bottom = GetOrCreateShaderMaterial("M_PoolBottom_Tile", "Game/PoolTile");
        ConfigureTile(bottom, PoolBottomTile, 0.5f); // 池底：焦散主舞台
        float t = WallThickness;

        // 四壁：内面分别位于 ±PoolWidth/2、±PoolDepth/2，从地板向下
        // 顶面抬高 ZFightOffset，避免与地板顶面共面重叠导致 z-fighting 闪烁
        float wallY = -PoolDepthY * 0.5f + ZFightOffset;

        CreateBox(parent, "PoolWall_North",
            new Vector3(0f, wallY, PoolDepth * 0.5f + t * 0.5f),
            new Vector3(PoolWidth, PoolDepthY, t), wall);
        CreateBox(parent, "PoolWall_South",
            new Vector3(0f, wallY, -(PoolDepth * 0.5f + t * 0.5f)),
            new Vector3(PoolWidth, PoolDepthY, t), wall);
        CreateBox(parent, "PoolWall_East",
            new Vector3(PoolWidth * 0.5f + t * 0.5f, wallY, 0f),
            new Vector3(t, PoolDepthY, PoolDepth), wall);
        CreateBox(parent, "PoolWall_West",
            new Vector3(-(PoolWidth * 0.5f + t * 0.5f), wallY, 0f),
            new Vector3(t, PoolDepthY, PoolDepth), wall);

        // 池底：顶面 y = -PoolDepthY
        CreateBox(parent, "PoolBottom",
            new Vector3(0f, -PoolDepthY - t * 0.5f, 0f),
            new Vector3(PoolWidth, t, PoolDepth), bottom);
    }

    // ================= 水面：代码生成细分平面 + 水面 shader =================
    private static void BuildWater(Transform parent)
    {
        Material waterMat = GetOrCreateShaderMaterial("M_WaterSurface", "Game/WaterSurface");
        // 水面参数（千禧年泳池的水：青碧蓝绿、随深度变深；_WaterColor.a 是水色 tint 强度）
        waterMat.SetColor("_WaterColor", new Color(0.15f, 0.52f, 0.60f, 0.55f));
        waterMat.SetColor("_DeepColor", new Color(0.02f, 0.16f, 0.40f, 1f));
        waterMat.SetFloat("_DepthScale", 1.5f); // 池深 1.5m 时深度占比 = 1
        waterMat.SetColor("_ReflectionColor", new Color(0.80f, 0.88f, 0.98f, 1f));
        waterMat.SetFloat("_WaveStrength", 0.02f);   // 微澜（镜面感需要更平静的水）
        waterMat.SetFloat("_WaveSpeed", 0.8f);
        waterMat.SetFloat("_RefractStrength", 0.012f); // 折射扭曲降低，池底更清透
        waterMat.SetFloat("_FresnelPower", 3.5f);
        waterMat.SetFloat("_Gloss", 512f);            // 波光更锐利
        waterMat.SetFloat("_SpecStrength", 0.8f);     // 波光更少（减少泛白光斑）
        waterMat.SetFloat("_Smoothness", 0.97f);      // 反射更光滑
        waterMat.SetFloat("_ReflectionWave", 0.05f);  // 倒影几乎镜面，仅 5% 涟漪
        waterMat.SetColor("_WaterlineColor", new Color(0.70f, 0.90f, 1.0f, 1f));
        waterMat.SetFloat("_WaterlineWidth", 0.15f);
        waterMat.SetFloat("_WaterlineStrength", 0.35f);
        // 水下焦散已移入池底/池壁材质（PoolTile），水面不再需要 caustics 参数

        // 8x4m 细分平面（40x20 段），y 局部为 0，父节点把整体放到水面高度
        Mesh mesh = CreatePlaneMesh(PoolWidth, PoolDepth, 40, 20, false);

        GameObject go = new GameObject("Water");
        go.transform.SetParent(parent, false);
        // 水面放在池沿下 25cm（真实泳池的水面高度感，露出更多干池壁）
        go.transform.localPosition = new Vector3(0f, -0.25f, 0f);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = waterMat;
        Undo.RegisterCreatedObjectUndo(go, "创建 Water");
    }

    // ================= 水上反射水光：天花板 + 四面墙的流动光斑 =================
    // 真实泳池里，阳光被水面反射后不只落在天花板，四周墙面也有（比天花板弱）。
    // 平面都贴在房间内表面朝内偏移一点（避免 z-fighting），additive 叠加。
    private static void BuildCaustics(Transform parent)
    {
        Material causticsMat = GetOrCreateShaderMaterial("M_Caustics", "Game/PoolCaustics");
        causticsMat.SetColor("_Color", new Color(0.75f, 0.92f, 1.0f, 1f));
        causticsMat.SetFloat("_Scale", 1.2f);
        causticsMat.SetFloat("_Speed", 0.8f);      // 与水面波浪同速
        causticsMat.SetFloat("_Distortion", 0.5f);
        causticsMat.SetFloat("_Sharpness", 3f);
        causticsMat.SetFloat("_Intensity", 0.2f);

        float halfH = RoomHeight * 0.5f;
        const float inset = 0.03f; // 向房间内偏移，避免与墙面共面

        // 天花板：法线朝下
        PlaceCausticsPanel(parent, causticsMat, "Caustics_Ceiling",
            CreatePlaneMesh(RoomWidth, RoomDepth, 30, 18, true),
            new Vector3(0f, RoomHeight - 0.02f, 0f), Quaternion.identity);

        // 北墙（内面 z = +6）：法线朝 +Z；网格 (宽=RoomWidth, 高=RoomHeight) 绕 X +90°
        PlaceCausticsPanel(parent, causticsMat, "Caustics_Wall_North",
            CreatePlaneMesh(RoomWidth, RoomHeight, 30, 8, false),
            new Vector3(0f, halfH, RoomDepth * 0.5f - inset), Quaternion.Euler(90f, 0f, 0f));

        // 南墙（内面 z = -6）：法线朝 -Z；绕 X -90°
        PlaceCausticsPanel(parent, causticsMat, "Caustics_Wall_South",
            CreatePlaneMesh(RoomWidth, RoomHeight, 30, 8, false),
            new Vector3(0f, halfH, -(RoomDepth * 0.5f - inset)), Quaternion.Euler(-90f, 0f, 0f));

        // 东墙（内面 x = +10）：法线朝 +X；网格 (宽=高, 深=深) 绕 Z -90°
        PlaceCausticsPanel(parent, causticsMat, "Caustics_Wall_East",
            CreatePlaneMesh(RoomHeight, RoomDepth, 8, 18, false),
            new Vector3(RoomWidth * 0.5f - inset, halfH, 0f), Quaternion.Euler(0f, 0f, -90f));

        // 西墙（内面 x = -10）：法线朝 -X；绕 Z +90°
        PlaceCausticsPanel(parent, causticsMat, "Caustics_Wall_West",
            CreatePlaneMesh(RoomHeight, RoomDepth, 8, 18, false),
            new Vector3(-(RoomWidth * 0.5f - inset), halfH, 0f), Quaternion.Euler(0f, 0f, 90f));
    }

    // ================= 场景道具：灯管阵列 + 池沿 + 爬梯 =================
    private static void BuildProps(Transform parent)
    {
        BuildLamps(parent);
        BuildCoping(parent);
        BuildLadder(parent);
    }

    // ---- 天花板灯管阵列（千禧年泳池标志性元素，也是水面反射的"亮内容"）----
    private static void BuildLamps(Transform parent)
    {
        Material lampMat = GetOrCreateMaterial("M_Lamp", new Color(0.98f, 0.99f, 1.0f, 1f));
        // URP/Lit 发光：亮白 + 轻微泛光
        lampMat.EnableKeyword("_EMISSION");
        lampMat.SetColor("_EmissionColor", new Color(1.6f, 1.7f, 1.9f));

        // 2 排 × 4 根，挂在天花板下方（天花板底面 y=5，灯管 y=4.82）
        const float lampY = 4.82f;
        float[] rowsZ = { -3.2f, 3.2f };
        float[] colsX = { -6f, -2f, 2f, 6f };
        foreach (float z in rowsZ)
        {
            foreach (float x in colsX)
            {
                GameObject lamp = CreateBox(parent, "Lamp",
                    new Vector3(x, lampY, z),
                    new Vector3(3.2f, 0.10f, 0.12f), lampMat);
                // 发光体不投影：避免灯管阴影把池壁顶面/池沿打灰
                lamp.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }
    }

    // ---- 池沿（coping）：池壁顶面一周的白色收边条 ----
    private static void BuildCoping(Transform parent)
    {
        Material copingMat = GetOrCreateMaterial("M_Coping", new Color(0.95f, 0.95f, 0.95f, 1f));
        // 池壁顶面 y=0.002（ZFightOffset），池沿略高于它
        const float copingY = 0.012f;
        const float over = 0.04f; // 比池壁顶面略宽出的部分

        // 长边（北/南）：池壁顶面宽 0.2m
        CreateBox(parent, "Coping_North",
            new Vector3(0f, copingY, PoolDepth * 0.5f + 0.1f),
            new Vector3(PoolWidth + over * 2f, 0.02f, 0.2f + over), copingMat);
        CreateBox(parent, "Coping_South",
            new Vector3(0f, copingY, -(PoolDepth * 0.5f + 0.1f)),
            new Vector3(PoolWidth + over * 2f, 0.02f, 0.2f + over), copingMat);

        // 短边（东/西）
        CreateBox(parent, "Coping_East",
            new Vector3(PoolWidth * 0.5f + 0.1f, copingY, 0f),
            new Vector3(0.2f + over, 0.02f, PoolDepth + over * 2f), copingMat);
        CreateBox(parent, "Coping_West",
            new Vector3(-(PoolWidth * 0.5f + 0.1f), copingY, 0f),
            new Vector3(0.2f + over, 0.02f, PoolDepth + over * 2f), copingMat);
    }

    // ---- 不锈钢爬梯：泳池南端池壁内侧，2 竖杆 + 4 横档 ----
    private static void BuildLadder(Transform parent)
    {
        // 普通银灰金属（不再追求全反射，反射链路折腾成本高、收益低）
        Material metalMat = GetOrCreateMaterial("M_Metal", new Color(0.72f, 0.74f, 0.78f, 1f));
        metalMat.SetFloat("_Smoothness", 0.85f);
        metalMat.SetFloat("_Metallic", 0.6f);

        // 位置：南端（z ≈ -1.7），两竖杆 X = ±0.3
        const float z = -1.7f;
        const float halfX = 0.3f;

        // 竖杆：从池底附近到露出水面（y 从 -1.35 到 0.1）
        CreateCylinder(parent, "Ladder_Pole_L",
            new Vector3(-halfX, -0.625f, z), new Vector3(0.06f, 0.725f, 0.06f), metalMat);
        CreateCylinder(parent, "Ladder_Pole_R",
            new Vector3(halfX, -0.625f, z), new Vector3(0.06f, 0.725f, 0.06f), metalMat);

        // 横档：4 根
        float[] steps = { -1.2f, -0.9f, -0.6f, -0.3f };
        for (int i = 0; i < steps.Length; i++)
        {
            CreateBox(parent, "Ladder_Step_" + i,
                new Vector3(0f, steps[i], z),
                new Vector3(halfX * 2f + 0.08f, 0.035f, 0.05f), metalMat);
        }
    }

    /// <summary>创建圆柱体（Primitive），并设置位置/缩放/材质。</summary>
    private static GameObject CreateCylinder(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        return go;
    }

    private static void PlaceCausticsPanel(Transform parent, Material mat, string name, Mesh mesh, Vector3 pos, Quaternion rot)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = rot;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
    }

    /// <summary>生成一块细分平面网格（y 局部为 0，可指定法线朝向）。</summary>
    private static Mesh CreatePlaneMesh(float width, float depth, int segX, int segZ, bool faceDown)
    {
        float halfW = width * 0.5f;
        float halfD = depth * 0.5f;

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        int stride = segX + 1;
        for (int z = 0; z <= segZ; z++)
        {
            for (int x = 0; x <= segX; x++)
            {
                float u = (float)x / segX;
                float v = (float)z / segZ;
                vertices.Add(new Vector3(Mathf.Lerp(-halfW, halfW, u), 0f, Mathf.Lerp(-halfD, halfD, v)));
                normals.Add(faceDown ? Vector3.down : Vector3.up);
                uvs.Add(new Vector2(u, v));
            }
        }

        for (int z = 0; z < segZ; z++)
        {
            for (int x = 0; x < segX; x++)
            {
                int i0 = z * stride + x;
                int i1 = i0 + 1;
                int i2 = i0 + stride;
                int i3 = i2 + 1;
                triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
            }
        }

        var mesh = new Mesh();
        mesh.name = faceDown ? "CausticsMesh" : "WaterMesh";
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ================= 房间四壁 + 天花板 =================
    private static void BuildWallsAndCeiling(Transform parent)
    {
        Material wall = GetOrCreateMaterial("M_RoomWall", WallColor);
        Material ceiling = GetOrCreateMaterial("M_Ceiling", CeilingColor);
        float t = WallThickness;
        float h = RoomHeight;

        CreateBox(parent, "Wall_North",
            new Vector3(0f, h * 0.5f, RoomDepth * 0.5f + t * 0.5f),
            new Vector3(RoomWidth, h, t), wall);
        CreateBox(parent, "Wall_South",
            new Vector3(0f, h * 0.5f, -(RoomDepth * 0.5f + t * 0.5f)),
            new Vector3(RoomWidth, h, t), wall);
        CreateBox(parent, "Wall_East",
            new Vector3(RoomWidth * 0.5f + t * 0.5f, h * 0.5f, 0f),
            new Vector3(t, h, RoomDepth), wall);
        CreateBox(parent, "Wall_West",
            new Vector3(-(RoomWidth * 0.5f + t * 0.5f), h * 0.5f, 0f),
            new Vector3(t, h, RoomDepth), wall);

        // 天花板：底面 y = RoomHeight
        CreateBox(parent, "Ceiling",
            new Vector3(0f, RoomHeight + t * 0.5f, 0f),
            new Vector3(RoomWidth, t, RoomDepth), ceiling);
    }

    // ================= 玩家（场景已有则复用） =================
    private static void EnsurePlayer()
    {
        if (GameObject.Find("Player") != null)
        {
            Debug.Log("[泳池灰盒] 场景已有 Player，已复用（可手动把它拖到池边）");
            return;
        }

        // 玩家站在北侧平台（z = 4.2），默认朝 -Z 看向池子
        GameObject player = new GameObject("Player");
        player.transform.position = new Vector3(0f, 0.1f, 4.2f);
        CharacterController cc = player.AddComponent<CharacterController>();
        cc.height = 2f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 1f, 0f);
        player.AddComponent<FirstPersonController>();
        player.AddComponent<PlayerInteractor>();
        Undo.RegisterCreatedObjectUndo(player, "创建 Player");

        // 主相机：必须是 Player 的子物体（FirstPersonController 的视角逻辑依赖此父子关系），位于人眼高度
        Camera cam = Camera.main;
        if (cam == null)
        {
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            Undo.RegisterCreatedObjectUndo(camGo, "创建 Main Camera");
        }

        Debug.Log("[泳池灰盒] 已创建 Player 与主相机");
    }

    // ================= 小工具 =================
    private static Transform CreateChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        return go.transform;
    }

    private static GameObject CreateBox(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        return go;
    }

    private static Material GetOrCreateMaterial(string name, Color color)
    {
        string path = MaterialDir + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            mat = new Material(shader) { color = color };
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log("[泳池灰盒] 创建材质 " + path);
        }
        else
        {
            // 复用时同步颜色，保证改常量后重新生成能生效
            mat.color = color;
        }
        return mat;
    }

    /// <summary>
    /// 创建（或复用）指定 shader 的材质。找不到 shader 时报错并回退 URP/Lit。
    /// 注意：材质参数由调用方各自配置（ConfigureTile / BuildWater 等）。
    /// </summary>
    private static Material GetOrCreateShaderMaterial(string name, string shaderName)
    {
        string path = MaterialDir + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("[泳池灰盒] 找不到 shader: " + shaderName + "，请确认对应 .shader 文件已导入");
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log("[泳池灰盒] 创建材质 " + path);
        }
        return mat;
    }

    /// <summary>同步瓷砖材质参数（白缝 + 15cm 方砖 + 釉面 + 水下焦散）。</summary>
    /// <param name="causticsIntensity">水下焦散强度（池底强、池壁弱；0 关闭）。</param>
    private static void ConfigureTile(Material mat, Color tileColor, float causticsIntensity)
    {
        mat.SetColor("_TileColor", tileColor);
        mat.SetColor("_GroutColor", new Color(0.92f, 0.93f, 0.94f, 1f));
        mat.SetFloat("_TileSize", 0.15f);
        mat.SetFloat("_GroutWidth", 0.08f);
        mat.SetFloat("_Smoothness", 0.6f);
        mat.SetFloat("_CausticsIntensity", causticsIntensity);
        mat.SetFloat("_CausticsScale", 1.2f);
        mat.SetFloat("_CausticsDistortion", 0.5f);
        mat.SetFloat("_CausticsSharpness", 3f);
        mat.SetFloat("_CausticsSpeed", 0.8f);
        mat.SetColor("_CausticsColor", new Color(0.60f, 0.85f, 1.0f, 1f));
        mat.SetFloat("_WaterLevel", -0.25f); // 与水面高度一致，以下才有焦散
    }

    /// <summary>
    /// 确保当前 URP Asset 开启水面需要的两个缓冲：
    ///   - Opaque Texture：折射采样 _CameraOpaqueTexture 的前提（并设为全分辨率，默认半分辨率会抹糊池底瓷砖）
    ///   - Depth Texture：深度变色采样 _CameraDepthTexture 的前提
    /// </summary>
    private static void EnsureURPTextures()
    {
        var rpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (rpAsset == null)
        {
            rpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        }
        if (rpAsset == null)
        {
            Debug.LogWarning("[泳池灰盒] 未找到 URP Asset，无法自动开启 Opaque/Depth Texture（水面折射与深度变色会失效）");
            return;
        }

        var so = new SerializedObject(rpAsset);

        // Opaque Texture（折射）
        SetUrpBool(so, "m_EnableOpaqueTexture", "Opaque Texture");

        // Opaque 全分辨率：Downsampling.None = 0
        SerializedProperty downsampleProp = so.FindProperty("m_OpaqueDownsampling");
        if (downsampleProp != null && downsampleProp.intValue != 0)
        {
            downsampleProp.intValue = 0;
            Debug.Log("[泳池灰盒] Opaque Texture 已改为全分辨率（池底细节不再被抹糊）");
        }

        // Depth Texture（深度变色 / 水线）
        SetUrpBool(so, "m_EnableDepthTexture", "Depth Texture");

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }

    /// <summary>把 URP Asset 上的某个 bool 属性打开（已是 true 则跳过）。</summary>
    private static void SetUrpBool(SerializedObject so, string propertyName, string displayName)
    {
        SerializedProperty prop = so.FindProperty(propertyName);
        if (prop != null && !prop.boolValue)
        {
            prop.boolValue = true;
            Debug.Log("[泳池灰盒] 已开启 URP " + displayName + "（水面效果需要）");
        }
    }

    /// <summary>找场景里第一盏方向光。</summary>
    private static Light FindDirectionalLight()
    {
        Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (Light l in lights)
        {
            if (l.type == LightType.Directional)
            {
                return l;
            }
        }
        return null;
    }

    /// <summary>
    /// 创建/复用 Global Volume，并确保后处理 Profile 包含：泛光（水面波光发光感）、
    /// ACES 色调映射（柔和胶片感）、轻微色调调整（lofi 基调）。
    /// </summary>
    private static void EnsureGlobalVolume()
    {
        const string profilePath = "Assets/Settings/Pool-AtmosphereProfile.asset";
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            Debug.Log("[泳池灰盒] 创建后处理 Profile " + profilePath);
        }

        // 泛光：让水面波光微微发光
        if (!profile.TryGet<Bloom>(out Bloom bloom))
        {
            bloom = profile.Add<Bloom>(true);
        }
        bloom.threshold.value = 1.1f;
        bloom.intensity.value = 0.4f;
        bloom.scatter.value = 0.4f;

        // ACES 色调映射
        if (!profile.TryGet<Tonemapping>(out Tonemapping tonemap))
        {
            tonemap = profile.Add<Tonemapping>(true);
        }
        tonemap.mode.value = TonemappingMode.ACES;

        // 轻色调调整
        if (!profile.TryGet<ColorAdjustments>(out ColorAdjustments ca))
        {
            ca = profile.Add<ColorAdjustments>(true);
        }
        ca.saturation.value = 1.02f;
        ca.contrast.value = 5f;

        // 挂到场景里的 Global Volume 上（没有则创建）
        Volume vol = Object.FindFirstObjectByType<Volume>();
        if (vol == null)
        {
            GameObject go = new GameObject("Global Volume");
            vol = go.AddComponent<Volume>();
            Undo.RegisterCreatedObjectUndo(go, "创建 Global Volume");
        }
        vol.isGlobal = true;
        vol.priority = 1f;
        vol.profile = profile;

        // 关键：Add 组件后必须标记 dirty 并保存，否则组件不会写进 asset（会得到空的 profile）
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
    }
}
