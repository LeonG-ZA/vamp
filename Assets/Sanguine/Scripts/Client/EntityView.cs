using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>Visual representation of a networked entity (another player, a mortal or a hunter).
    /// Smoothly follows the latest server snapshot.</summary>
    public sealed class EntityView : MonoBehaviour
    {
        public int Id;
        public EntityKind Kind;
        public EntityInfoMsg Info;
        public EntityFlags Flags;
        public float HealthPct = 1f;
        public float LastSeen;

        private Vector3 _targetPos;
        private float _targetRot;
        private Transform _body;
        private GameObject _crown, _markLight, _bloodPool;
        private Renderer _bodyRenderer;
        private bool _hasPosition;

        public static EntityView Create(int id, EntityKind kind, Transform parent)
        {
            var go = new GameObject(kind + " #" + id);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<EntityView>();
            v.Id = id;
            v.Kind = kind;
            v.BuildVisual();
            return v;
        }

        /// <summary>Builds a simple humanoid out of primitives. Also used for the local player.</summary>
        public static Transform BuildAvatar(Transform parent, Color coat, Color skin, out Renderer bodyRenderer)
        {
            var body = new GameObject("Body").transform;
            body.SetParent(parent, false);
            GameObject torso = Mats.Prim(PrimitiveType.Capsule, body, new Vector3(0, 1f, 0), new Vector3(0.7f, 0.85f, 0.7f), coat, false);
            bodyRenderer = torso.GetComponent<Renderer>();
            Mats.Prim(PrimitiveType.Sphere, body, new Vector3(0, 1.95f, 0), new Vector3(0.42f, 0.46f, 0.42f), skin, false);
            // Eyes so facing is readable.
            Mats.Prim(PrimitiveType.Sphere, body, new Vector3(-0.09f, 2.0f, 0.18f), Vector3.one * 0.07f, new Color(0.9f, 0.1f, 0.1f), false, true);
            Mats.Prim(PrimitiveType.Sphere, body, new Vector3(0.09f, 2.0f, 0.18f), Vector3.one * 0.07f, new Color(0.9f, 0.1f, 0.1f), false, true);
            return body;
        }

        private void BuildVisual()
        {
            Color coat, skin = new Color(0.85f, 0.75f, 0.68f);
            var rng = new Rng(Id * 31 + 7);
            switch (Kind)
            {
                case EntityKind.Mortal:
                    coat = Color.HSVToRGB(rng.NextFloat(), rng.Range(0.2f, 0.6f), rng.Range(0.3f, 0.7f));
                    skin = Color.Lerp(new Color(0.95f, 0.8f, 0.7f), new Color(0.4f, 0.27f, 0.18f), rng.NextFloat());
                    break;
                case EntityKind.Hunter:
                    coat = new Color(0.25f, 0.18f, 0.1f);
                    break;
                default:
                    coat = new Color(0.12f, 0.12f, 0.14f);
                    skin = new Color(0.93f, 0.9f, 0.92f);
                    break;
            }

            _body = BuildAvatar(transform, coat, skin, out _bodyRenderer);

            if (Kind == EntityKind.Mortal)
            {
                // Mortal eyes are not glowing red.
                foreach (Transform c in _body)
                    if (c.localScale.x < 0.1f) c.GetComponent<Renderer>().sharedMaterial = Mats.Get(new Color(0.1f, 0.1f, 0.1f));
            }

            if (Kind == EntityKind.Hunter)
            {
                Mats.Prim(PrimitiveType.Cylinder, _body, new Vector3(0, 2.2f, 0), new Vector3(0.6f, 0.05f, 0.6f), new Color(0.1f, 0.08f, 0.05f), false);
                Mats.Prim(PrimitiveType.Cylinder, _body, new Vector3(0, 2.35f, 0), new Vector3(0.35f, 0.15f, 0.35f), new Color(0.1f, 0.08f, 0.05f), false);
                GameObject torch = Mats.Prim(PrimitiveType.Sphere, _body, new Vector3(0.45f, 1.6f, 0.3f), Vector3.one * 0.2f, new Color(1f, 0.55f, 0.15f), false, true);
                Light l = torch.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.55f, 0.2f);
                l.range = 8f;
                l.intensity = 2f;
            }

            if (Kind == EntityKind.Player)
            {
                _crown = Mats.Prim(PrimitiveType.Cylinder, _body, new Vector3(0, 2.3f, 0), new Vector3(0.38f, 0.08f, 0.38f), new Color(1f, 0.8f, 0.2f), false, true);
                _crown.SetActive(false);
                _markLight = new GameObject("Mark");
                _markLight.transform.SetParent(_body, false);
                _markLight.transform.localPosition = new Vector3(0, 3f, 0);
                Light ml = _markLight.AddComponent<Light>();
                ml.type = LightType.Point;
                ml.color = Color.red;
                ml.range = 6f;
                ml.intensity = 3f;
                _markLight.SetActive(false);
            }

            _bloodPool = Mats.Prim(PrimitiveType.Cylinder, transform, new Vector3(0, 0.13f, 0), new Vector3(1.6f, 0.01f, 1.6f), new Color(0.3f, 0f, 0f), false);
            _bloodPool.SetActive(false);

            // Trigger collider for mouse picking; triggers do not block the character controller.
            var cap = gameObject.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.center = new Vector3(0, 1f, 0);
            cap.height = 2.2f;
            cap.radius = 0.5f;
        }

        public void ApplyInfo(EntityInfoMsg info)
        {
            Info = info;
            if (Kind == EntityKind.Player && _bodyRenderer != null)
                _bodyRenderer.sharedMaterial = Mats.Get(Color.Lerp(Mats.BloodlineColor(info.Bloodline), Color.black, 0.45f));
        }

        public void ApplyState(EntityState s, float now)
        {
            _targetPos = new Vector3(s.X, 0.12f, s.Z);
            _targetRot = s.Rot;
            Flags = s.Flags;
            HealthPct = s.HealthPct / 255f;
            LastSeen = now;
            if (!_hasPosition)
            {
                _hasPosition = true;
                transform.position = _targetPos;
                transform.rotation = Quaternion.Euler(0, _targetRot, 0);
            }
        }

        private void Update()
        {
            if (!_hasPosition) return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            if ((transform.position - _targetPos).sqrMagnitude > 100f) transform.position = _targetPos;
            else transform.position = Vector3.Lerp(transform.position, _targetPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0, _targetRot, 0), k);

            bool down = (Flags & EntityFlags.Down) != 0;
            bool feeding = (Flags & EntityFlags.Feeding) != 0;
            Quaternion pose = Quaternion.identity;
            Vector3 offset = Vector3.zero;
            if (down) { pose = Quaternion.Euler(-90, 0, 0); offset = new Vector3(0, 0.35f, 0f); }
            else if (feeding) pose = Quaternion.Euler(Kind == EntityKind.Player ? 18f : -12f, 0, 0);
            _body.localRotation = Quaternion.Slerp(_body.localRotation, pose, k);
            _body.localPosition = Vector3.Lerp(_body.localPosition, offset, k);

            _bloodPool.SetActive(down && Kind != EntityKind.Player);
            if (_crown != null) _crown.SetActive((Flags & EntityFlags.Regent) != 0);
            if (_markLight != null) _markLight.SetActive((Flags & EntityFlags.Marked) != 0);
        }

        public string DisplayName
        {
            get
            {
                if (Info.Name == null) return Kind.ToString();
                if (Kind == EntityKind.Player && !string.IsNullOrEmpty(Info.Circle)) return Info.Name + " <" + Info.Circle + ">";
                return Info.Name;
            }
        }

        public Color LabelColor
        {
            get
            {
                if (Kind == EntityKind.Hunter) return new Color(1f, 0.6f, 0.2f);
                if (Kind == EntityKind.Mortal) return (Flags & EntityFlags.Alarmed) != 0 ? new Color(1f, 0.9f, 0.4f) : new Color(0.8f, 0.8f, 0.8f);
                if ((Flags & EntityFlags.Marked) != 0) return new Color(1f, 0.25f, 0.25f);
                return Color.Lerp(Mats.BloodlineColor(Info.Bloodline), Color.white, 0.35f);
            }
        }
    }
}
