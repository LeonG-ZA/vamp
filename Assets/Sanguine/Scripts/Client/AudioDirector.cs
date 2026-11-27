using System.Collections.Generic;
using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>
    /// The city's soundscape, driven entirely by state the client already replicates:
    /// day/night ambience, rain and score; fire-barrel and neon loops at the nearest emitters;
    /// surface-dependent footsteps for the player and nearby NPCs; and gameplay cues (feeding,
    /// heartbeat, strikes, bloodline gifts, torpor, sunlight, danger stings, the dawn bell).
    /// Clips are synthesised by Tools/audio/synth_audio.py into Resources/Audio.
    /// </summary>
    public sealed class AudioDirector : MonoBehaviour
    {
        public GameClient Client;
        public static AudioDirector Instance { get; private set; }

        public static float MasterVolume
        {
            get { return PlayerPrefs.GetFloat("sanguine.vol.master", 0.8f); }
            set { PlayerPrefs.SetFloat("sanguine.vol.master", Mathf.Clamp01(value)); }
        }

        public static float MusicVolume
        {
            get { return PlayerPrefs.GetFloat("sanguine.vol.music", 0.5f); }
            set { PlayerPrefs.SetFloat("sanguine.vol.music", Mathf.Clamp01(value)); }
        }

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private AudioSource _ambNight, _ambDay, _rain, _music, _heartbeat, _sizzle;
        private AudioLowPassFilter[] _muffle;
        private readonly List<AudioSource> _oneShots = new List<AudioSource>();
        private int _nextOneShot;
        private readonly List<AudioSource> _emitterSources = new List<AudioSource>();
        private readonly List<KeyValuePair<Vector3, LightKind>> _emitters = new List<KeyValuePair<Vector3, LightKind>>();
        private float _nextEmitterScan;
        private readonly int[] _emitterOf = { -1, -1, -1, -1, -1, -1 };

        private float _day, _indoors, _feedTime;
        private SelfFlags _prevFlags;
        private float _prevAttackCd, _prevPowerCd;
        private bool _prevIsDay, _haveWorld, _haveSelf;
        private float _lastSting;

        private Vector3 _lastLocalPos;
        private float _localStride;
        private readonly Dictionary<int, Vector3> _viewPos = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _viewStride = new Dictionary<int, float>();
        private readonly Dictionary<int, EntityFlags> _viewFlags = new Dictionary<int, EntityFlags>();
        private readonly List<int> _stale = new List<int>();

        private void Awake()
        {
            Instance = this;
            _ambNight = Loop("amb_night_city", 0.7f);
            _ambDay = Loop("amb_day_city", 0f);
            _rain = Loop("amb_rain", 0.5f);
            _music = Loop("music_night", 0f);
            _heartbeat = Loop("loop_heartbeat", 0f);
            _sizzle = Loop("loop_sizzle", 0f);
            _muffle = new[] { _ambNight.GetComponent<AudioLowPassFilter>(), _ambDay.GetComponent<AudioLowPassFilter>(), _rain.GetComponent<AudioLowPassFilter>() };
            for (int i = 0; i < 12; i++) _oneShots.Add(NewSource("One-shot", false));
            for (int i = 0; i < _emitterOf.Length; i++) _emitterSources.Add(NewSource("Emitter", true));
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ set-up helpers

        private AudioClip Clip(string name)
        {
            AudioClip c;
            if (!_clips.TryGetValue(name, out c))
            {
                c = Resources.Load<AudioClip>("Audio/" + name);
                _clips[name] = c;
            }
            return c;
        }

        private AudioSource NewSource(string name, bool spatial)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.dopplerLevel = 0f;
            if (spatial)
            {
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 1.5f;
                s.maxDistance = 16f;
            }
            return s;
        }

        private AudioSource Loop(string clip, float volume)
        {
            AudioSource s = NewSource(clip, false);
            s.clip = Clip(clip);
            s.loop = true;
            s.volume = volume;
            s.gameObject.AddComponent<AudioLowPassFilter>().cutoffFrequency = 22000f;
            if (s.clip != null) { s.time = Random.value * s.clip.length * 0.9f; s.Play(); }
            return s;
        }

        /// <summary>Plays a clip once, either flat (position null) or in 3D at a world position.</summary>
        public void Play(string clip, float volume, Vector3? position = null, float pitch = 1f, float maxDistance = 25f)
        {
            AudioClip c = Clip(clip);
            if (c == null) return;
            AudioSource s = _oneShots[_nextOneShot];
            _nextOneShot = (_nextOneShot + 1) % _oneShots.Count;
            s.Stop();
            s.clip = c;
            s.volume = volume;
            s.pitch = pitch;
            if (position.HasValue)
            {
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 1f;
                s.maxDistance = maxDistance;
                s.transform.position = position.Value;
            }
            else
            {
                s.spatialBlend = 0f;
                s.transform.localPosition = Vector3.zero;
            }
            s.Play();
        }

        public void Click()
        {
            Play("ui_click", 0.5f);
        }

        /// <summary>Called by the city builder with the positions of sound-emitting lights.</summary>
        public void SetEmitters(IEnumerable<LightPlacement> lights)
        {
            _emitters.Clear();
            for (int i = 0; i < _emitterOf.Length; i++) _emitterOf[i] = -1;
            foreach (AudioSource s in _emitterSources) if (s != null) s.Stop();
            foreach (LightPlacement l in lights)
                if (l.Kind == LightKind.Fire || l.Kind == LightKind.Neon)
                    _emitters.Add(new KeyValuePair<Vector3, LightKind>(new Vector3(l.X, l.Y - 0.6f, l.Z), l.Kind));
        }

        /// <summary>Gameplay notices with sound: danger stings and good-news chimes (rate limited).</summary>
        public void OnNotice(NoticeLevel level)
        {
            if (Time.time - _lastSting < 2.5f) return;
            if (level == NoticeLevel.Danger) { Play("sting_danger", 0.55f); _lastSting = Time.time; }
            else if (level == NoticeLevel.Good) { Play("sting_good", 0.45f); _lastSting = Time.time; }
        }

        // ------------------------------------------------------------------ per frame

        private void Update()
        {
            AudioListener.volume = MasterVolume;
            float dt = Time.unscaledDeltaTime;
            bool inGame = Client != null && Client.State == ClientState.InGame;

            // Day/night blend (matches Atmosphere): fade over the last 20 s before the change.
            float dayTarget = 0f;
            WorldStateMsg w = Client != null ? Client.World : null;
            if (w != null)
            {
                float f = Mathf.Clamp01(1f - w.SecondsToChange / 20f);
                dayTarget = w.IsDay ? 1f - f * 0.8f : f * 0.8f;
                if (_haveWorld && w.IsDay && !_prevIsDay) Play("sfx_dawn_bell", 0.8f);
                _prevIsDay = w.IsDay;
                _haveWorld = true;
            }
            _day = Mathf.MoveTowards(_day, dayTarget, dt * 0.5f);

            SelfStateMsg self = inGame ? Client.Self : null;
            bool indoors = self != null && (self.Flags & (SelfFlags.InHaven | SelfFlags.InSanctum)) != 0;
            _indoors = Mathf.MoveTowards(_indoors, indoors ? 1f : 0f, dt * 2f);
            float cutoff = Mathf.Lerp(22000f, 900f, _indoors);
            foreach (AudioLowPassFilter lp in _muffle) if (lp != null) lp.cutoffFrequency = cutoff;

            float outside = 1f - 0.55f * _indoors;
            _ambNight.volume = 0.75f * (1f - _day) * outside;
            _ambDay.volume = 0.6f * _day * outside;
            _rain.volume = 0.5f * (1f - _day) * outside;
            _music.volume = MusicVolume * (inGame ? 0.55f : 0.8f) * (1f - _day * 0.7f);
            if (!_music.isPlaying && _music.clip != null) _music.Play();

            if (self != null) UpdateSelf(self, dt);
            else
            {
                _heartbeat.volume = Mathf.MoveTowards(_heartbeat.volume, 0f, dt);
                _sizzle.volume = 0f;
                _haveSelf = false;
            }

            if (inGame)
            {
                UpdateFootsteps();
                if (Time.unscaledTime >= _nextEmitterScan)
                {
                    _nextEmitterScan = Time.unscaledTime + 0.5f;
                    UpdateEmitters();
                }
            }
        }

        private void UpdateSelf(SelfStateMsg s, float dt)
        {
            bool feeding = (s.Flags & SelfFlags.Feeding) != 0;
            if (_haveSelf)
            {
                if (feeding && (_prevFlags & SelfFlags.Feeding) == 0) { Play("sfx_bite", 0.9f); _feedTime = 0f; }
                if ((s.Flags & SelfFlags.Torpor) != 0 && (_prevFlags & SelfFlags.Torpor) == 0) Play("sfx_torpor", 0.9f);
                if ((s.Flags & SelfFlags.Marked) != 0 && (_prevFlags & SelfFlags.Marked) == 0) { Play("sting_danger", 0.7f); _lastSting = Time.time; }
                // A cooldown that jumps from zero means the server accepted the action.
                if (s.AttackCooldown > _prevAttackCd + 0.3f) Play("sfx_strike", 0.8f, null, Random.Range(0.92f, 1.08f));
                if (s.PowerCooldown > _prevPowerCd + 0.5f) Play(PowerClip(Client.MyBloodline), 0.85f);
            }
            _prevFlags = s.Flags;
            _prevAttackCd = s.AttackCooldown;
            _prevPowerCd = s.PowerCooldown;
            _haveSelf = true;

            // Heartbeat: the victim's while feeding (quickening), the vampire's own when starving.
            bool starving = (s.Flags & SelfFlags.Starving) != 0;
            if (feeding) _feedTime += dt;
            float hbTarget = feeding ? 0.85f : (starving ? 0.45f : 0f);
            _heartbeat.volume = Mathf.MoveTowards(_heartbeat.volume, hbTarget, dt * 1.5f);
            _heartbeat.pitch = feeding ? Mathf.Lerp(1f, 1.45f, _feedTime / 5f) : 0.8f;
            _sizzle.volume = Mathf.MoveTowards(_sizzle.volume, (s.Flags & SelfFlags.Sunlight) != 0 ? 0.6f : 0f, dt * 3f);
        }

        private static string PowerClip(Bloodline b)
        {
            switch (b)
            {
                case Bloodline.Vorlen: return "power_surge";
                case Bloodline.Sylphine: return "power_allure";
                case Bloodline.Ashborn: return "power_lash";
                default: return "power_command";
            }
        }

        /// <summary>Footstep clip for the ground at a point: cobbles in the Old Quarter, wet asphalt on roads.</summary>
        public static string StepClip(Vector3 p)
        {
            int n = Random.Range(1, 5);
            bool sidewalk = CityDressing.GroundHeight(p.x, p.z) > 0.01f;
            if (GameData.DistrictAt(p.x, p.z) == 2) return "step_cobble_" + n;
            return (sidewalk ? "step_hard_" : "step_wet_") + n;
        }

        /// <summary>Distance between footfalls: ~0.7 m when walking, up to ~1.7 m at a run.</summary>
        private static float StrideFor(float speed)
        {
            return Mathf.Clamp(0.45f + 0.2f * speed, 0.65f, 1.7f);
        }

        private void UpdateFootsteps()
        {
            // The local vampire: a step every ~0.75 m (faster gait = longer stride).
            if (Client.Local != null)
            {
                Vector3 p = Client.Local.transform.position;
                Vector3 d = p - _lastLocalPos;
                d.y = 0f;
                _lastLocalPos = p;
                float dist = d.magnitude;
                if (dist < 2f)
                {
                    _localStride += dist;
                    if (_localStride > StrideFor(dist / Mathf.Max(Time.deltaTime, 1e-4f)))
                    { _localStride = 0f; Play(StepClip(p), 0.55f, null, Random.Range(0.94f, 1.06f)); }
                }
            }

            // Nearby NPCs and players: positional steps, and a thud when someone falls.
            Camera cam = Camera.main;
            Vector3 ear = cam != null ? cam.transform.position : Vector3.zero;
            _stale.Clear();
            foreach (int id in _viewPos.Keys) if (!Client.Views.ContainsKey(id)) _stale.Add(id);
            foreach (int id in _stale) { _viewPos.Remove(id); _viewStride.Remove(id); _viewFlags.Remove(id); }

            foreach (KeyValuePair<int, EntityView> kv in Client.Views)
            {
                EntityView v = kv.Value;
                if (v == null) continue;
                Vector3 p = v.transform.position;
                Vector3 last;
                if (!_viewPos.TryGetValue(kv.Key, out last)) last = p;
                _viewPos[kv.Key] = p;

                EntityFlags prev;
                if (_viewFlags.TryGetValue(kv.Key, out prev) && (v.Flags & EntityFlags.Down) != 0 && (prev & EntityFlags.Down) == 0 &&
                    (p - ear).sqrMagnitude < 40f * 40f)
                    Play("sfx_thud", 0.8f, p, 1f, 30f);
                _viewFlags[kv.Key] = v.Flags;

                if ((p - ear).sqrMagnitude > 22f * 22f) continue;
                Vector3 d = p - last;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist > 2f) continue; // teleport / snap
                float stride;
                _viewStride.TryGetValue(kv.Key, out stride);
                stride += dist;
                if (stride > StrideFor(dist / Mathf.Max(Time.deltaTime, 1e-4f)))
                {
                    stride = 0f;
                    Play(StepClip(p), 0.35f, p, Random.Range(0.9f, 1.1f), 18f);
                }
                _viewStride[kv.Key] = stride;
            }
        }

        private void UpdateEmitters()
        {
            Camera cam = Camera.main;
            if (cam == null || _emitters.Count == 0) return;
            Vector3 ear = cam.transform.position;
            var near = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < _emitters.Count; i++)
            {
                float d = (_emitters[i].Key - ear).sqrMagnitude;
                if (d < 26f * 26f) near.Add(new KeyValuePair<float, int>(d, i));
            }
            near.Sort((a, b) => a.Key.CompareTo(b.Key));
            var wanted = new HashSet<int>();
            for (int i = 0; i < near.Count && i < _emitterSources.Count; i++) wanted.Add(near[i].Value);

            // Keep sources whose emitter is still wanted (no restart); free the rest.
            var free = new List<int>();
            for (int s = 0; s < _emitterSources.Count; s++)
            {
                if (_emitterOf[s] >= 0 && wanted.Contains(_emitterOf[s])) wanted.Remove(_emitterOf[s]);
                else { _emitterOf[s] = -1; if (_emitterSources[s].isPlaying) _emitterSources[s].Stop(); free.Add(s); }
            }
            foreach (int e in wanted)
            {
                if (free.Count == 0) break;
                int s = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                KeyValuePair<Vector3, LightKind> em = _emitters[e];
                AudioClip c = Clip(em.Value == LightKind.Fire ? "loop_fire" : "loop_neon");
                if (c == null) continue;
                AudioSource src = _emitterSources[s];
                src.clip = c;
                src.loop = true;
                src.transform.position = em.Key;
                src.maxDistance = em.Value == LightKind.Fire ? 14f : 9f;
                src.volume = em.Value == LightKind.Fire ? 0.7f : 0.35f;
                src.time = Random.value * c.length * 0.9f;
                src.Play();
                _emitterOf[s] = e;
            }
        }
    }
}
