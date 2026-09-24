using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 泳池 demo 打包脚本（平台可选）。
/// 用法：
///   - 编辑器内：菜单 Tools → 泳池灰盒 → 打包 Windows / 打包 macOS
///   - 命令行（批处理）：
///     Unity -batchmode -quit -projectPath <项目路径> -executeMethod BuildScript.BuildWindows -logFile build.log
/// 输出：
///   - Windows: Builds/Windows/PoolDemo.exe
///   - macOS:   Builds/macOS/PoolDemo.app
/// 注意：在 macOS 上构建 Windows 版需要安装 "Windows Build Support (Mono)" 模块；
///       本机 Win11 构建则无需额外模块（Windows 目标原生支持）。
/// </summary>
public static class BuildScript
{
    private const string PoolScenePath = "Assets/Game/Scenes/Pool-01.unity";

    [MenuItem("Tools/泳池灰盒/打包 Windows")]
    public static void BuildWindows()
    {
        Build(BuildTarget.StandaloneWindows64, "Builds/Windows", "PoolDemo.exe");
    }

    [MenuItem("Tools/泳池灰盒/打包 macOS")]
    public static void BuildMac()
    {
        Build(BuildTarget.StandaloneOSX, "Builds/macOS", "PoolDemo.app");
    }

    private static void Build(BuildTarget target, string outputRoot, string appName)
    {
        // 1. 把 Pool-01 设为唯一的构建场景
        if (!File.Exists(PoolScenePath))
        {
            Debug.LogError("[打包] 找不到场景 " + PoolScenePath);
            return;
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(PoolScenePath, true) };

        // 2. 清空旧输出，避免残留
        string fullOutput = Path.Combine(outputRoot, appName);
        if (Directory.Exists(fullOutput))
        {
            Directory.Delete(fullOutput, true);
        }
        Directory.CreateDirectory(outputRoot);

        // 3. 构建
        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { PoolScenePath },
            locationPathName = fullOutput,
            target = target,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log("[打包] ✅ 构建成功（" + target + "）：" + summary.outputPath +
                      "（大小 " + (summary.totalSize / 1024f / 1024f).ToString("0.0") + " MB）");
        }
        else
        {
            Debug.LogError("[打包] ❌ 构建失败：" + summary.result + "，错误 " + summary.totalErrors + " 个");
        }
    }
}
