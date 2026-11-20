using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>Drives lighting and fog from the server's night/day cycle.</summary>
    public sealed class Atmosphere : MonoBehaviour
    {
        public GameClient Client;
        private Light _sun;
        private float _dayAmount; // 0 = deep night, 1 = full day

        private static readonly Color NightFog = new Color(0.035f, 0.03f, 0.06f);
        private static readonly Color DayFog = new Color(0.62f, 0.58f, 0.52f);
        private static readonly Color NightAmbient = new Color(0.1f, 0.09f, 0.16f);
        private static readonly Color DayAmbient = new Color(0.55f, 0.52f, 0.48f);

        private void Start()
        {
            var go = new GameObject("Moon / Sun");
            go.transform.SetParent(transform, false);
            _sun = go.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.skybox = null;
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

            _sun.color = Color.Lerp(new Color(0.5f, 0.55f, 0.9f), new Color(1f, 0.93f, 0.8f), d);
            _sun.intensity = Mathf.Lerp(0.3f, 1.2f, d);
            _sun.transform.rotation = Quaternion.Euler(Mathf.Lerp(35f, 55f, d), w != null ? w.CyclePhase * 360f : 30f, 0);
            RenderSettings.ambientLight = Color.Lerp(NightAmbient, DayAmbient, d);
            Color fog = Color.Lerp(NightFog, DayFog, d);
            RenderSettings.fogColor = fog;
            RenderSettings.fogDensity = Mathf.Lerp(0.011f, 0.004f, d);

            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = fog;
            }
        }
    }
}
