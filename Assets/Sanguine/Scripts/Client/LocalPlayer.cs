using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>The locally controlled vampire: third-person movement and an orbit camera.
    /// Movement is predicted locally and validated by the server (which may send a Teleport to correct it).</summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        public GameClient Client;
        public Camera Cam;

        private CharacterController _cc;
        private CharacterVisual _visual;
        private GiftAura _aura;
        private float _yaw, _pitch = 18f, _distance = 7f;
        private float _verticalVelocity;
        private float _nextSend;
        private Vector3 _lastSentPos;
        private float _lastSentRot;

        public static LocalPlayer Create(GameClient client, Bloodline bloodline, Vector3 position)
        {
            var go = new GameObject("Local Player");
            go.transform.position = position;
            var lp = go.AddComponent<LocalPlayer>();
            lp.Client = client;

            lp._cc = go.AddComponent<CharacterController>();
            lp._cc.height = 2f;
            lp._cc.radius = 0.4f;
            lp._cc.center = new Vector3(0, 1f, 0);
            lp._cc.stepOffset = 0.4f;

            lp._visual = CharacterVisual.Create(go.transform, CharacterRole.Vampire, client.MyId, bloodline);
            lp._aura = GiftAura.Attach(go, lp._visual.gameObject);

            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            cam.fieldOfView = 60f;
            PostFx.Setup(cam);
            lp.Cam = cam;
            return lp;
        }

        public void Teleport(Vector3 pos)
        {
            _cc.enabled = false;
            transform.position = pos;
            _cc.enabled = true;
            _lastSentPos = pos;
            _verticalVelocity = 0;
        }

        private void Update()
        {
            if (Client == null || Client.Self == null) return;
            SelfFlags f = Client.Self.Flags;

            // --- Camera orbit -------------------------------------------------
            if (!Client.UiCapturesMouse && (Input.GetMouseButton(1) || Input.GetMouseButton(2)))
            {
                _yaw += Input.GetAxis("Mouse X") * 4f;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 3f, -10f, 70f);
            }
            if (!Client.UiCapturesMouse) _distance = Mathf.Clamp(_distance - Input.mouseScrollDelta.y * 0.8f, 3f, 18f);
            if (!Client.ChatOpen)
            {
                if (Input.GetKey(KeyCode.Q)) _yaw -= 90f * Time.deltaTime;
                if (Input.GetKey(KeyCode.E)) _yaw += 90f * Time.deltaTime;
            }

            // --- Movement -----------------------------------------------------
            Vector3 input = Vector3.zero;
            if (!Client.ChatOpen)
            {
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) input.z += 1;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) input.z -= 1;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) input.x += 1;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) input.x -= 1;
            }
            bool frozen = (f & (SelfFlags.Torpor | SelfFlags.Rooted)) != 0;
            float speed = GameRules.WalkSpeed;
            if ((f & SelfFlags.Surge) != 0) speed *= GameRules.SurgeSpeedMultiplier;
            if ((f & SelfFlags.Slowed) != 0) speed *= GameRules.SlowSpeedMultiplier;
            if ((f & SelfFlags.Mist) != 0) speed *= 1.3f;
            if ((f & SelfFlags.Feeding) != 0) speed = 0f; // moving would break the embrace; press F to release first
            if (frozen) speed = 0f;

            Vector3 move = Vector3.zero;
            if (input.sqrMagnitude > 0.01f && speed > 0f)
            {
                Quaternion yawRot = Quaternion.Euler(0, _yaw, 0);
                move = yawRot * input.normalized * speed;
                Quaternion face = Quaternion.LookRotation(move.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, face, 1f - Mathf.Exp(-14f * Time.deltaTime));
            }
            if (_cc.isGrounded) _verticalVelocity = -1f; else _verticalVelocity -= 20f * Time.deltaTime;
            move.y = _verticalVelocity;
            _cc.Move(move * Time.deltaTime);

            // Animation from the actual ground speed.
            Vector3 v = _cc.velocity;
            v.y = 0f;
            _visual.SetMotion(v.magnitude, (f & SelfFlags.Feeding) != 0, (f & SelfFlags.Torpor) != 0);
            _aura.Show((f & SelfFlags.Mist) != 0, (f & SelfFlags.Majesty) != 0, (f & SelfFlags.IronSkin) != 0);

            // --- Network ------------------------------------------------------
            if (Time.time >= _nextSend)
            {
                _nextSend = Time.time + 1f / 15f;
                Vector3 p = transform.position;
                float rot = transform.eulerAngles.y;
                if ((p - _lastSentPos).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(rot, _lastSentRot)) > 2f)
                {
                    _lastSentPos = p;
                    _lastSentRot = rot;
                    Client.Send(SimpleMsg.Move(p.x, p.z, rot));
                }
            }
        }

        private void LateUpdate()
        {
            if (Cam == null) return;
            Vector3 pivot = transform.position + Vector3.up * 1.8f;
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0);
            Vector3 desired = pivot - rot * Vector3.forward * _distance;
            RaycastHit hit;
            Vector3 dir = desired - pivot;
            if (Physics.SphereCast(pivot, 0.25f, dir.normalized, out hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore))
                desired = pivot + dir.normalized * Mathf.Max(0.8f, hit.distance);
            Cam.transform.position = desired;
            Cam.transform.LookAt(pivot);
        }
    }
}
