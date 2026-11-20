using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>
    /// Drives sky, moon/sun light, fog, ambient light and reflections from the server's night/day cycle.
    /// Uses the HDR skies (Poly Haven) through the Sanguine/Sky shader when available.
    /// </summary>
    public sealed class Atmosphere : MonoBehaviour
    {
        public GameClient Client;

        private const float SkyRotation = 0f;
        private Light _sun;
        private Material _sky;
        private ReflectionProbe _probe;
        private Vector3 _moonDir = new Vector3(0.3f, 0.85f, 0.4f).normalized;
        private float _dayAmount;     // 0 = deep night, 1 = full day
        private float _lastGiAmount = -1f;
        private float _nextProbe;

        private static readonly Color NightFog = new Color(0.045f, 0.05f, 0.075f);
        private static readonly Color DayFog = new Color(0.55f, 0.58f, 0.62f);
        private static readonly Color NightAmbient = new Color(0.13f, 0.14f, 0.19f);
        private static readonly Color DayAmbient = new Color(0.5f, 0.52f, 0.55f);

        private void Start()
        {
            var go = new GameObject("Moon / Sun");
            go.transform.SetParent(transform, false);
            _sun = go.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            _sun.shadowStrength = 0.8f;
            _sun.shadowBias = 0.03f;
            RenderSettings.sun = _sun;

            ArtLibrary.Load();
            Texture2D night = ArtLibrary.SkyTexture(true), day = ArtLibrary.SkyTexture(false);
            RenderAssets ra = RenderAssets.Instance;
            Shader shader = ra != null && ra.skyShader != null ? ra.skyShader : Shader.Find("Sanguine/Sky");
            if (shader != null && night != null)
            {
                _sky = new Material(shader);
                _sky.SetTexture("_NightTex", night);
                _sky.SetTexture("_DayTex", day != null ? day : night);
                _sky.SetFloat("_Rotation", SkyRotation);
                RenderSettings.skybox = _sky;
                if (ArtLibrary.Sky != null) _moonDir = ArtLibrary.SkyDirection(ArtLibrary.Sky.moonU, ArtLibrary.Sky.moonV, SkyRotation);
            }
            else
            {
                RenderSettings.skybox = null;
            }
            // Ambient fill: scattered city light from above, warmer at street level, dark below.
            // (The HDR sky still provides the background and reflections.)
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

            QualitySettings.shadowDistance = 140f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);

            // Realtime reflections around the player: wet streets mirror lamps, neon and windows.
            var probeGo = new GameObject("Street Reflections");
            probeGo.transform.SetParent(transform, false);
            _probe = probeGo.AddComponent<ReflectionProbe>();
            _probe.mode = ReflectionProbeMode.Realtime;
            _probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            _probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            _probe.resolution = 128;
            _probe.size = new Vector3(160f, 60f, 160f);
            _probe.boxProjection = false;
            _probe.hdr = true;
            _probe.farClipPlane = 220f;
            _probe.shadowDistance = 0f;
            _probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        }

        private void Update()
        {
            float target = 0f;
            WorldStateMsg w = Client != null ? Client.World : null;
            if (w != null)
            {
                // Fade over the last 20 seconds before a change.
                float fade = Mathf.Clamp01(1f - w.SecondsToChange / 20f);
                target = w.IsDay ? 1f - fade * 0.8f : fade * 0.8f;
            }
            _dayAmount = Mathf.MoveTowards(_dayAmount, target, Time.deltaTime * 0.5f);
            float d = _dayAmount;

            // Moonlight comes from the moon in the sky texture; the sun from high above at noon.
            Vector3 sunDir = Vector3.Slerp(_moonDir, new Vector3(-0.35f, 0.8f, 0.45f).normalized, d);
            _sun.transform.rotation = Quaternion.LookRotation(-sunDir);
            _sun.color = Color.Lerp(new Color(0.55f, 0.65f, 0.95f), new Color(1f, 0.94f, 0.84f), d);
            _sun.intensity = Mathf.Lerp(0.32f, 1.25f, d);

            Color fog = Color.Lerp(NightFog, DayFog, d);
            RenderSettings.fogColor = fog;
            RenderSettings.fogDensity = Mathf.Lerp(0.0085f, 0.0035f, d);

            Camera cam = Camera.main;
            if (_sky != null)
            {
                _sky.SetFloat("_DayBlend", d);
                // The night HDR is exposure-normalised (its sky is nearly as bright as the day sky's),
                // so it is scaled down hard to read as night.
                _sky.SetFloat("_NightExposure", 0.06f);
                _sky.SetFloat("_DayExposure", 1.0f);
                _sky.SetFloat("_Clamp", 40f);
                if (Mathf.Abs(d - _lastGiAmount) > 0.02f)
                {
                    _lastGiAmount = d;
                    DynamicGI.UpdateEnvironment();
                }
                if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
            }
            RenderSettings.ambientSkyColor = Color.Lerp(NightAmbient, DayAmbient, d);
            RenderSettings.ambientEquatorColor = Color.Lerp(new Color(0.12f, 0.105f, 0.095f), new Color(0.45f, 0.44f, 0.42f), d);
            RenderSettings.ambientGroundColor = Color.Lerp(new Color(0.045f, 0.04f, 0.038f), new Color(0.22f, 0.21f, 0.2f), d);
            RenderSettings.ambientLight = RenderSettings.ambientSkyColor;
            if (_sky == null)
            {
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = fog;
                }
            }

            if (cam != null && _probe != null && Time.unscaledTime >= _nextProbe)
            {
                _nextProbe = Time.unscaledTime + 0.5f;
                _probe.transform.position = cam.transform.position;
                _probe.RenderProbe();
            }
        }
    }
}
