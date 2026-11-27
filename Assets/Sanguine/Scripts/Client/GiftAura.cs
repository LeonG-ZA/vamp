using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>Visible signs of active gifts on a character: Crimson Mist (the body dissolves into
    /// swirling red motes), Majesty (a warm golden radiance) and Iron Skin (a cold steel glint).</summary>
    public sealed class GiftAura : MonoBehaviour
    {
        private GameObject _body;
        private Transform _mist;
        private Light _mistLight, _majesty, _iron;
        private bool _isMist;

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

        public void Show(bool mist, bool majesty, bool ironSkin)
        {
            if (mist != _isMist)
            {
                _isMist = mist;
                if (_body != null) _body.SetActive(!mist);
                _mist.gameObject.SetActive(mist);
            }
            _majesty.enabled = majesty;
            _iron.enabled = ironSkin;
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
            if (_majesty.enabled) _majesty.intensity = 2.0f + 0.4f * Mathf.Sin(t * 2.2f);
            if (_iron.enabled) _iron.intensity = 1.2f + 0.8f * Mathf.Abs(Mathf.Sin(t * 3.1f));
        }
    }
}
