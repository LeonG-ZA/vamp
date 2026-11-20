using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>
    /// Fallback human figure used when no rigged character art is installed: a jointed body with
    /// realistic proportions and a procedural walk/run cycle, feeding lean and collapse.
    /// </summary>
    public sealed class ProceduralFigure : MonoBehaviour
    {
        public Transform Head;
        public Transform RightHand;

        private Transform _hips, _spine, _neck, _lThigh, _lShin, _rThigh, _rShin, _lUpper, _lFore, _rUpper, _rFore;
        private float _phase, _lean, _down, _breath;
        private const float HipHeight = 0.93f;

        private static Transform Joint(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        /// <summary>A limb segment hanging down from its joint.</summary>
        private static void Segment(Transform joint, float length, float radius, Color c, float smoothness = 0.25f)
        {
            GameObject g = Mats.Prim(PrimitiveType.Capsule, joint, new Vector3(0f, -length * 0.5f, 0f),
                                     new Vector3(radius * 2f, length * 0.5f + radius * 0.3f, radius * 2f), c, false);
            g.GetComponent<Renderer>().sharedMaterial = Mats.Get(c, false, smoothness);
        }

        public static ProceduralFigure Build(Transform parent, CharacterRole role, int seed, Bloodline bloodline)
        {
            var root = new GameObject("Figure").transform;
            root.SetParent(parent, false);
            var f = root.gameObject.AddComponent<ProceduralFigure>();
            var rng = new Rng(seed * 31 + 7);

            Color skin, top, legs, shoes = new Color(0.05f, 0.05f, 0.055f), coat = Color.clear, hair;
            float build = rng.Range(0.92f, 1.08f), height = rng.Range(0.95f, 1.06f);
            switch (role)
            {
                case CharacterRole.Vampire:
                    Color bc = Mats.BloodlineColor(bloodline);
                    skin = new Color(0.86f, 0.82f, 0.84f);
                    top = Color.Lerp(bc, new Color(0.05f, 0.04f, 0.05f), 0.65f);
                    legs = new Color(0.06f, 0.06f, 0.07f);
                    coat = Color.Lerp(new Color(0.04f, 0.035f, 0.04f), bc, 0.18f);
                    hair = new Color(0.04f, 0.03f, 0.03f);
                    break;
                case CharacterRole.Hunter:
                    skin = Color.Lerp(new Color(0.9f, 0.74f, 0.62f), new Color(0.45f, 0.3f, 0.2f), rng.NextFloat());
                    top = new Color(0.2f, 0.22f, 0.18f);
                    legs = new Color(0.12f, 0.13f, 0.11f);
                    coat = new Color(0.24f, 0.18f, 0.11f);
                    hair = new Color(0.12f, 0.09f, 0.06f);
                    break;
                default:
                    skin = Color.Lerp(new Color(0.95f, 0.8f, 0.7f), new Color(0.36f, 0.24f, 0.16f), rng.NextFloat());
                    top = Color.HSVToRGB(rng.NextFloat(), rng.Range(0.15f, 0.55f), rng.Range(0.18f, 0.6f));
                    legs = Color.HSVToRGB(rng.Range(0.55f, 0.7f), rng.Range(0.1f, 0.5f), rng.Range(0.08f, 0.35f));
                    if (rng.NextFloat() < 0.35f) coat = Color.HSVToRGB(rng.NextFloat(), rng.Range(0.1f, 0.35f), rng.Range(0.12f, 0.3f));
                    hair = Color.Lerp(new Color(0.06f, 0.04f, 0.03f), new Color(0.6f, 0.45f, 0.25f), rng.NextFloat() * rng.NextFloat());
                    break;
            }
            root.localScale = new Vector3(build, height, build);

            f._hips = Joint("Hips", root, new Vector3(0f, HipHeight, 0f));
            Mats.Prim(PrimitiveType.Capsule, f._hips, new Vector3(0f, 0.02f, 0f), new Vector3(0.34f, 0.12f, 0.22f), legs, false);
            f._spine = Joint("Spine", f._hips, new Vector3(0f, 0.08f, 0f));
            Mats.Prim(PrimitiveType.Capsule, f._spine, new Vector3(0f, 0.25f, 0f), new Vector3(0.38f, 0.27f, 0.23f), top, false);
            f._neck = Joint("Neck", f._spine, new Vector3(0f, 0.52f, 0f));
            Mats.Prim(PrimitiveType.Cylinder, f._neck, new Vector3(0f, 0.04f, 0f), new Vector3(0.1f, 0.05f, 0.1f), skin, false);
            f.Head = Joint("Head", f._neck, new Vector3(0f, 0.17f, 0.01f));
            Mats.Prim(PrimitiveType.Sphere, f.Head, Vector3.zero, new Vector3(0.19f, 0.235f, 0.21f), skin, false)
                .GetComponent<Renderer>().sharedMaterial = Mats.Get(skin, false, 0.35f);
            Mats.Prim(PrimitiveType.Sphere, f.Head, new Vector3(0f, 0.045f, -0.015f), new Vector3(0.2f, 0.17f, 0.21f), hair, false);
            Color eye = role == CharacterRole.Vampire ? new Color(0.9f, 0.08f, 0.08f) : new Color(0.06f, 0.05f, 0.05f);
            Mats.Prim(PrimitiveType.Sphere, f.Head, new Vector3(-0.038f, 0.015f, 0.092f), Vector3.one * 0.026f, eye, false, role == CharacterRole.Vampire);
            Mats.Prim(PrimitiveType.Sphere, f.Head, new Vector3(0.038f, 0.015f, 0.092f), Vector3.one * 0.026f, eye, false, role == CharacterRole.Vampire);

            // Arms.
            Color sleeve = coat.a > 0f ? coat : top;
            f._lUpper = Joint("L Arm", f._spine, new Vector3(-0.22f, 0.47f, 0f));
            Segment(f._lUpper, 0.29f, 0.055f, sleeve);
            f._lFore = Joint("L Forearm", f._lUpper, new Vector3(0f, -0.29f, 0f));
            Segment(f._lFore, 0.26f, 0.048f, sleeve);
            Mats.Prim(PrimitiveType.Sphere, f._lFore, new Vector3(0f, -0.3f, 0f), new Vector3(0.07f, 0.09f, 0.05f), skin, false);
            f._rUpper = Joint("R Arm", f._spine, new Vector3(0.22f, 0.47f, 0f));
            Segment(f._rUpper, 0.29f, 0.055f, sleeve);
            f._rFore = Joint("R Forearm", f._rUpper, new Vector3(0f, -0.29f, 0f));
            Segment(f._rFore, 0.26f, 0.048f, sleeve);
            Mats.Prim(PrimitiveType.Sphere, f._rFore, new Vector3(0f, -0.3f, 0f), new Vector3(0.07f, 0.09f, 0.05f), skin, false);
            f.RightHand = Joint("R Hand", f._rFore, new Vector3(0f, -0.31f, 0.02f));

            // Legs.
            f._lThigh = Joint("L Thigh", f._hips, new Vector3(-0.095f, 0f, 0f));
            Segment(f._lThigh, 0.45f, 0.075f, legs);
            f._lShin = Joint("L Shin", f._lThigh, new Vector3(0f, -0.45f, 0f));
            Segment(f._lShin, 0.43f, 0.058f, legs);
            Mats.Prim(PrimitiveType.Cube, f._lShin, new Vector3(0f, -0.45f, 0.05f), new Vector3(0.1f, 0.07f, 0.26f), shoes, false);
            f._rThigh = Joint("R Thigh", f._hips, new Vector3(0.095f, 0f, 0f));
            Segment(f._rThigh, 0.45f, 0.075f, legs);
            f._rShin = Joint("R Shin", f._rThigh, new Vector3(0f, -0.45f, 0f));
            Segment(f._rShin, 0.43f, 0.058f, legs);
            Mats.Prim(PrimitiveType.Cube, f._rShin, new Vector3(0f, -0.45f, 0.05f), new Vector3(0.1f, 0.07f, 0.26f), shoes, false);

            // Long coat: body plus a skirt that hangs from the hips.
            if (coat.a > 0f)
            {
                Mats.Prim(PrimitiveType.Capsule, f._spine, new Vector3(0f, 0.24f, 0f), new Vector3(0.41f, 0.29f, 0.26f), coat, false);
                Mats.Prim(PrimitiveType.Cylinder, f._hips, new Vector3(0f, -0.26f, 0f), new Vector3(0.42f, 0.27f, 0.3f), coat, false);
                Mats.Prim(PrimitiveType.Cube, f._spine, new Vector3(0f, 0.5f, -0.02f), new Vector3(0.3f, 0.1f, 0.2f), coat, false);
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return f;
        }

        public void Animate(float speed, bool feeding, bool down, float dt)
        {
            float move = Mathf.Clamp01(speed / 1.3f);
            bool running = speed > 3f;
            float strideLength = running ? 2.4f : 1.4f;
            _phase += speed * dt / strideLength * Mathf.PI * 2f;
            _breath += dt * 1.6f;

            float legAmp = (running ? 42f : 28f) * move;
            float armAmp = (running ? 50f : 24f) * move;
            float s = Mathf.Sin(_phase), c = Mathf.Cos(_phase);

            float targetLean = feeding ? 28f : (running ? 12f : 3f * move);
            _lean = Mathf.MoveTowards(_lean, targetLean, dt * 80f);
            _down = Mathf.MoveTowards(_down, down ? 1f : 0f, dt * 2.5f);

            _lThigh.localRotation = Quaternion.Euler(-s * legAmp + (feeding ? -12f : 0f), 0f, 0f);
            _rThigh.localRotation = Quaternion.Euler(s * legAmp + (feeding ? 8f : 0f), 0f, 0f);
            _lShin.localRotation = Quaternion.Euler(Mathf.Max(0f, c) * legAmp * 1.3f + (feeding ? 18f : 0f), 0f, 0f);
            _rShin.localRotation = Quaternion.Euler(Mathf.Max(0f, -c) * legAmp * 1.3f + (feeding ? 10f : 0f), 0f, 0f);

            if (feeding)
            {
                // Arms reach forward to hold the victim.
                _lUpper.localRotation = Quaternion.Euler(-70f, 0f, 14f);
                _rUpper.localRotation = Quaternion.Euler(-65f, 0f, -14f);
                _lFore.localRotation = Quaternion.Euler(-35f, 0f, 0f);
                _rFore.localRotation = Quaternion.Euler(-40f, 0f, 0f);
            }
            else
            {
                float idleSway = Mathf.Sin(_breath) * 2f * (1f - move);
                _lUpper.localRotation = Quaternion.Euler(s * armAmp + idleSway, 0f, 6f);
                _rUpper.localRotation = Quaternion.Euler(-s * armAmp - idleSway, 0f, -6f);
                _lFore.localRotation = Quaternion.Euler(-(running ? 70f : 12f + 10f * move), 0f, 0f);
                _rFore.localRotation = Quaternion.Euler(-(running ? 70f : 12f + 10f * move), 0f, 0f);
            }

            _spine.localRotation = Quaternion.Euler(_lean + Mathf.Sin(_breath) * 0.8f, Mathf.Sin(_phase) * 4f * move, 0f);
            _neck.localRotation = Quaternion.Euler(feeding ? 22f : -_lean * 0.4f, 0f, 0f);
            float bob = Mathf.Abs(s) * (running ? 0.05f : 0.025f) * move;
            _hips.localPosition = new Vector3(0f, HipHeight - (feeding ? 0.1f : 0f) + bob, 0f);

            // Collapse onto the back when in torpor or dead.
            transform.localRotation = Quaternion.Euler(-88f * _down, 0f, 0f);
            transform.localPosition = new Vector3(0f, 0.16f * _down, -0.0f);
        }
    }
}
