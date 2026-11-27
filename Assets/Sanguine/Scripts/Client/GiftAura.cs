using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>Visible signs of active gifts on a character: Crimson Mist (the body dissolves into
    /// swirling red motes), Majesty (a warm golden radiance), Iron Skin (a cold steel glint),
    /// Shroud (a body of drifting shadow) and Unchained (a feral ember glow).</summary>
    public sealed class GiftAura : MonoBehaviour
    {
        private GameObject _body;
        private Transform _mist, _shadow;
        private Light _mistLight, _majesty, _iron, _shadowLight, _beast;
        private bool _isMist, _isShadow, _isBeast;

        public static GiftAura Attach(GameObject root, GameObject body)
        {
            var a = root.AddComponent<GiftAura>();
            a._body = body;

            a._mist = new GameObject("Crimson Mist").transform;
            a._mist.SetParent(root.transform, false);
            for (int i = 0; i < 14; i++)
            {
                float k = i / 14f;
                Mats.Prim(PrimitiveType.Sphere, a._mist, new Vector3(Mathf.Cos(k * 6.283f) * 0.5f, 0.3f + k * 1.4f, Mathf.Sin(k * 6.283f) * 0.5f),
                          Vector3.one * (0.18f + 0.12f * Mathf.Sin(i * 1.7f)), new Color(0.45f, 0.02f, 0.04f), false, true);
            }
            a._mistLight = Light(a._mist, new Vector3(0, 1f, 0), new Color(0.9f, 0.08f, 0.1f), 5f, 2f);
            a._mist.gameObject.SetActive(false);

            a._majesty = Light(root.transform, new Vector3(0, 2.4f, 0), new Color(1f, 0.82f, 0.45f), 7f, 2.2f);
            a._iron = Light(root.transform, new Vector3(0.4f, 1.5f, 0.4f), new Color(0.65f, 0.78f, 1f), 3.5f, 1.6f);
            a._majesty.enabled = a._iron.enabled = false;

            // Shroud: a figure-shaped column of slow, near-black motes.
            a._shadow = new GameObject("Shroud").transform;
            a._shadow.SetParent(root.transform, false);
            for (int i = 0; i < 22; i++)
            {
                float k = i / 22f;
                float rad = 0.18f + 0.12f * Mathf.Sin(i * 2.3f);
                Mats.Prim(PrimitiveType.Sphere, a._shadow, new Vector3(Mathf.Cos(k * 18.85f) * rad, 0.15f + k * 1.65f, Mathf.Sin(k * 18.85f) * rad),
                          Vector3.one * (0.22f + 0.1f * Mathf.Sin(i * 1.3f)), new Color(0.02f, 0.04f, 0.05f), false);
            }
            a._shadowLight = Light(a._shadow, new Vector3(0, 1.2f, 0), new Color(0.25f, 0.6f, 0.65f), 3f, 0.5f);
            a._shadow.gameObject.SetActive(false);

            a._beast = Light(root.transform, new Vector3(0f, 1.3f, 0.5f), new Color(1f, 0.35f, 0.08f), 5f, 2f);
            a._beast.enabled = false;
            return a;
        }

        private static Light Light(Transform parent, Vector3 pos, Color c, float range, float intensity)
        {
            var go = new GameObject("Gift light");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.range = range;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            return l;
        }

        public void Show(bool mist, bool majesty, bool ironSkin, bool shroud = false, bool unchained = false)
        {
            shroud &= !mist;
            if (mist != _isMist || shroud != _isShadow)
            {
                _isMist = mist;
                _isShadow = shroud;
                if (_body != null) _body.SetActive(!mist && !shroud);
                _mist.gameObject.SetActive(mist);
                _shadow.gameObject.SetActive(shroud);
            }
            if (unchained != _isBeast)
            {
                _isBeast = unchained;
                if (_body != null) _body.transform.localScale = Vector3.one * (unchained ? 1.08f : 1f);
            }
            _majesty.enabled = majesty;
            _iron.enabled = ironSkin;
            _beast.enabled = unchained;
        }

        private void Update()
        {
            float t = Time.time;
            if (_isMist)
            {
                _mist.localRotation = Quaternion.Euler(0f, t * 140f, 0f);
                _mist.localScale = Vector3.one * (1f + 0.12f * Mathf.Sin(t * 5f));
                _mistLight.intensity = 1.6f + 0.6f * Mathf.Sin(t * 9f);
            }
            if (_isShadow)
            {
                _shadow.localRotation = Quaternion.Euler(0f, t * 35f, 0f);
                _shadowLight.intensity = 0.4f + 0.15f * Mathf.Sin(t * 1.7f);
            }
            if (_isBeast) _beast.intensity = 1.6f + 1.0f * Mathf.PerlinNoise(t * 6f, 0.3f);
            if (_majesty.enabled) _majesty.intensity = 2.0f + 0.4f * Mathf.Sin(t * 2.2f);
            if (_iron.enabled) _iron.intensity = 1.2f + 0.8f * Mathf.Abs(Mathf.Sin(t * 3.1f));
        }
    }
}
