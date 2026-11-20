using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>Visual representation of a networked entity (another player, a mortal or a hunter).
    /// Smoothly follows the latest server snapshot, stands on the real ground (curbs included) and
    /// animates from its movement speed.</summary>
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
        private CharacterVisual _visual;
        private GameObject _crown, _markLight, _bloodPool;
        private bool _hasPosition;
        private Vector3 _lastPos;
        private float _speed;

        public static EntityView Create(int id, EntityKind kind, Transform parent, EntityInfoMsg? info)
        {
            var go = new GameObject(kind + " #" + id);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<EntityView>();
            v.Id = id;
            v.Kind = kind;
            if (info.HasValue) v.Info = info.Value;
            v.BuildVisual();
            return v;
        }

        private void BuildVisual()
        {
            CharacterRole role = Kind == EntityKind.Player ? CharacterRole.Vampire : (Kind == EntityKind.Hunter ? CharacterRole.Hunter : CharacterRole.Mortal);
            _visual = CharacterVisual.Create(transform, role, Id, Info.Bloodline);

            if (Kind == EntityKind.Hunter && _visual.RightHand != null)
            {
                // A flashlight sweeping the street.
                var torch = new GameObject("Flashlight");
                torch.transform.SetParent(_visual.RightHand, false);
                torch.transform.localPosition = new Vector3(0f, -0.04f, 0.06f);
                torch.transform.localRotation = Quaternion.Euler(-15f, 0f, 0f);
                Mats.Prim(PrimitiveType.Cylinder, torch.transform, new Vector3(0f, 0f, 0.05f), new Vector3(0.045f, 0.09f, 0.045f), new Color(0.08f, 0.08f, 0.08f), false)
                    .transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var beam = new GameObject("Beam");
                beam.transform.SetParent(transform, false);
                beam.transform.localPosition = new Vector3(0.25f, 1.35f, 0.3f);
                beam.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
                Light l = beam.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = new Color(0.9f, 0.95f, 1f);
                l.range = 20f;
                l.spotAngle = 38f;
                l.intensity = 4f;
                l.shadows = LightShadows.None;
            }

            if (Kind == EntityKind.Player)
            {
                Transform head = _visual.Head != null ? _visual.Head : transform;
                _crown = Mats.Prim(PrimitiveType.Cylinder, head, new Vector3(0, 0.16f, 0), new Vector3(0.24f, 0.035f, 0.24f), new Color(1f, 0.8f, 0.2f), false, true);
                _crown.SetActive(false);
                _markLight = new GameObject("Mark");
                _markLight.transform.SetParent(transform, false);
                _markLight.transform.localPosition = new Vector3(0, 2.6f, 0);
                Light ml = _markLight.AddComponent<Light>();
                ml.type = LightType.Point;
                ml.color = Color.red;
                ml.range = 6f;
                ml.intensity = 3f;
                _markLight.SetActive(false);
            }

            _bloodPool = Mats.Prim(PrimitiveType.Cylinder, transform, new Vector3(0, 0.005f, -0.9f), new Vector3(1.4f, 0.004f, 1.1f), new Color(0.16f, 0f, 0f), false);
            _bloodPool.GetComponent<Renderer>().sharedMaterial = Mats.Get(new Color(0.16f, 0f, 0f), false, 0.85f);
            _bloodPool.SetActive(false);

            // Trigger collider for mouse picking; triggers do not block the character controller.
            var cap = gameObject.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.center = new Vector3(0, 0.95f, 0);
            cap.height = 1.9f;
            cap.radius = 0.45f;
        }

        public void ApplyInfo(EntityInfoMsg info)
        {
            Info = info;
        }

        public void ApplyState(EntityState s, float now)
        {
            _targetPos = new Vector3(s.X, CityDressing.GroundHeight(s.X, s.Z), s.Z);
            _targetRot = s.Rot;
            Flags = s.Flags;
            HealthPct = s.HealthPct / 255f;
            LastSeen = now;
            if (!_hasPosition)
            {
                _hasPosition = true;
                transform.position = _targetPos;
                transform.rotation = Quaternion.Euler(0, _targetRot, 0);
                _lastPos = _targetPos;
            }
        }

        private void Update()
        {
            if (!_hasPosition) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float k = 1f - Mathf.Exp(-12f * dt);
            if ((transform.position - _targetPos).sqrMagnitude > 100f) transform.position = _targetPos;
            else transform.position = Vector3.Lerp(transform.position, _targetPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0, _targetRot, 0), k);

            Vector3 delta = transform.position - _lastPos;
            delta.y = 0f;
            _lastPos = transform.position;
            _speed = Mathf.Lerp(_speed, delta.magnitude / dt, 1f - Mathf.Exp(-8f * dt));

            bool down = (Flags & EntityFlags.Down) != 0;
            bool feeding = (Flags & EntityFlags.Feeding) != 0 && Kind == EntityKind.Player;
            _visual.SetMotion(down ? 0f : _speed, feeding, down);

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
