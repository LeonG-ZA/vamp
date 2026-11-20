using UnityEngine;
#if SANGUINE_PPV2
using UnityEngine.Rendering.PostProcessing;
#endif

namespace Sanguine.Client
{
    /// <summary>
    /// Camera set-up for a physically based night city: HDR, deferred shading (hundreds of street lights)
    /// and, when the Post Processing package is installed, bloom, ambient occlusion, screen-space
    /// reflections, ACES tonemapping, vignette and film grain.
    /// </summary>
    public static class PostFx
    {
        public static void Setup(Camera cam)
        {
            if (cam == null) return;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.renderingPath = RenderingPath.DeferredShading;
            cam.farClipPlane = 900f;
            cam.nearClipPlane = 0.15f;
#if SANGUINE_PPV2
            RenderAssets ra = RenderAssets.Instance;
            PostProcessResources resources = ra != null ? ra.postProcessResources : null;
            if (resources == null)
            {
                Debug.LogWarning("[Sanguine] Post-processing resources not found; running without bloom/AO/SSR.");
                return;
            }
            if (cam.GetComponent<PostProcessLayer>() != null) return; // already configured
            PostProcessLayer layer = cam.gameObject.AddComponent<PostProcessLayer>();
            layer.Init(resources);
            layer.volumeTrigger = cam.transform;
            layer.volumeLayer = ~0;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
            layer.subpixelMorphologicalAntialiasing.quality = SubpixelMorphologicalAntialiasing.Quality.High;

            var volumeGo = new GameObject("Post-processing Volume");
            Object.DontDestroyOnLoad(volumeGo);
            var volume = volumeGo.AddComponent<PostProcessVolume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            var profile = ScriptableObject.CreateInstance<PostProcessProfile>();

            var bloom = profile.AddSettings<Bloom>();
            bloom.enabled.Override(true);
            bloom.intensity.Override(2.2f);
            bloom.threshold.Override(1.05f);
            bloom.softKnee.Override(0.6f);
            bloom.diffusion.Override(7.5f);

            var ao = profile.AddSettings<AmbientOcclusion>();
            ao.enabled.Override(true);
            ao.mode.Override(AmbientOcclusionMode.MultiScaleVolumetricObscurance);
            ao.intensity.Override(0.75f);
            ao.thicknessModifier.Override(1.5f);

            var ssr = profile.AddSettings<ScreenSpaceReflections>();
            ssr.enabled.Override(true);
            ssr.preset.Override(ScreenSpaceReflectionPreset.Medium);
            ssr.maximumMarchDistance.Override(120f);
            ssr.distanceFade.Override(0.15f);
            ssr.vignette.Override(0.3f);

            var grading = profile.AddSettings<ColorGrading>();
            grading.enabled.Override(true);
            grading.tonemapper.Override(Tonemapper.ACES);
            grading.postExposure.Override(0.55f);
            grading.temperature.Override(-6f);
            grading.saturation.Override(-6f);
            grading.contrast.Override(12f);

            var vignette = profile.AddSettings<Vignette>();
            vignette.enabled.Override(true);
            vignette.intensity.Override(0.32f);
            vignette.smoothness.Override(0.45f);

            var grain = profile.AddSettings<Grain>();
            grain.enabled.Override(true);
            grain.intensity.Override(0.16f);
            grain.size.Override(0.9f);

            volume.sharedProfile = profile;
#endif
        }
    }
}
