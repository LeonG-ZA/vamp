using System;
using Sanguine.Server;
using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>
    /// Zero-setup entry point: pressing Play in any scene (even an empty one) starts the game.
    /// Launching a build with -server (or in batch mode) runs a headless dedicated server instead.
    /// </summary>
    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (UnityEngine.Object.FindFirstObjectByType<GameClient>() != null) return;
            if (UnityEngine.Object.FindFirstObjectByType<DedicatedServerHost>() != null) return;

            string[] args = Environment.GetCommandLineArgs();
            bool serverMode = Application.isBatchMode || Array.IndexOf(args, "-server") >= 0;
            var go = new GameObject(serverMode ? "Sanguine Dedicated Server" : "Sanguine City");
            UnityEngine.Object.DontDestroyOnLoad(go);
            if (serverMode) go.AddComponent<DedicatedServerHost>();
            else go.AddComponent<GameClient>();
        }
    }

    /// <summary>Runs the shared server inside a headless Unity player (e.g. a Linux Dedicated Server build).</summary>
    public sealed class DedicatedServerHost : MonoBehaviour
    {
        private GameServer _server;

        private void Start()
        {
            Application.targetFrameRate = 10;
            ServerConfig cfg = ServerConfig.FromArgs(Environment.GetCommandLineArgs());
            cfg.Log = s => Debug.Log(s);
            _server = new GameServer(cfg);
            _server.Start();
        }

        private void OnApplicationQuit()
        {
            if (_server != null) _server.Stop();
        }
    }
}
