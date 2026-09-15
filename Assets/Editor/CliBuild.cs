using UnityEditor;
using UnityEditor.Build.Reporting;

/// <summary>命令行批量打包入口：Unity.exe -batchmode -executeMethod CliBuild.BuildAndroid</summary>
public static class CliBuild
{
    public static void BuildAndroid()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = "Builds/VoiceAI.apk",
            target = BuildTarget.Android
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
        EditorApplication.Exit(0);
    }
}
