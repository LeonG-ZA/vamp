using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Sanguine.EditorTools
{
    /// <summary>Editor helpers: create the (empty) main scene and make player / dedicated server builds.
    /// The game bootstraps itself from code, so the scene needs no content.</summary>
    public static class SanguineMenu
    {
        private const string ScenePath = "Assets/Sanguine/Scenes/Main.unity";

        [MenuItem("Sanguine/Create Main Scene")]
        public static void CreateMainScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.Refresh();
        }

        private static void EnsureScene()
        {
            if (!File.Exists(ScenePath)) CreateMainScene();
            if (EditorBuildSettings.scenes.Length == 0)
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        [MenuItem("Sanguine/Build/Windows Client")]
        public static void BuildWindows() { Build(BuildTarget.StandaloneWindows64, "Builds/Windows/SanguineCity.exe", false); }

        [MenuItem("Sanguine/Build/macOS Client")]
        public static void BuildMac() { Build(BuildTarget.StandaloneOSX, "Builds/macOS/SanguineCity.app", false); }

        [MenuItem("Sanguine/Build/Linux Client")]
        public static void BuildLinux() { Build(BuildTarget.StandaloneLinux64, "Builds/Linux/SanguineCity.x86_64", false); }

        [MenuItem("Sanguine/Build/Linux Dedicated Server")]
        public static void BuildLinuxServer() { Build(BuildTarget.StandaloneLinux64, "Builds/LinuxServer/SanguineServer.x86_64", true); }

        private static void Build(BuildTarget target, string path, bool server)
        {
            EnsureScene();
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };
            if (server) opts.subtarget = (int)StandaloneBuildSubtarget.Server;
            var report = BuildPipeline.BuildPlayer(opts);
            UnityEngine.Debug.Log("Sanguine build " + report.summary.result + ": " + path);
        }
    }
}
