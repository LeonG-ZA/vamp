using UnityEditor;
using UnityEngine;

namespace Sanguine.EditorTools
{
    /// <summary>One-time project settings needed for the physically based art.</summary>
    [InitializeOnLoad]
    public static class SanguineProjectSetup
    {
        static SanguineProjectSetup()
        {
            EditorApplication.delayCall += Apply;
        }

        private static void Apply()
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                Debug.Log("[Sanguine] Switched the project to Linear colour space for physically based rendering.");
            }
        }
    }
}
