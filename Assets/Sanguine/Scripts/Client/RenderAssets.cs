using UnityEngine;
#if SANGUINE_PPV2
using UnityEngine.Rendering.PostProcessing;
#endif

namespace Sanguine.Client
{
    /// <summary>
    /// References to engine assets that must ship in player builds: template materials (so the
    /// Standard shader keeps its normal-map / emission variants), the sky shader and the
    /// post-processing resources. Stored at Resources/SanguineRenderAssets.asset.
    /// </summary>
    public sealed class RenderAssets : ScriptableObject
    {
        public Material plainTemplate;
        public Material emissiveTemplate;
        public Material pbrTemplate;
        public Material pbrEmissiveTemplate;
        public Shader skyShader;
#if SANGUINE_PPV2
        public PostProcessResources postProcessResources;
#endif

        private static RenderAssets _instance;
        private static bool _searched;

        public static RenderAssets Instance
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _instance = Resources.Load<RenderAssets>("SanguineRenderAssets");
                }
                return _instance;
            }
        }
    }
}
