using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Sanguine.Client
{
    public enum CharacterRole { Vampire, Mortal, Hunter }

    /// <summary>
    /// Finds drop-in rigged human characters and animation clips:
    ///   Resources/Characters/Vampires|Mortals|Hunters|Any/  — rigged models (imported as Humanoid)
    ///   Resources/Animations/                              — clips named e.g. Idle, Walk, Run, Feed/Bite, Death
    /// Any humanoid rig works (Mixamo, Rocketbox, MakeHuman exports...) thanks to Humanoid retargeting.
    /// </summary>
    public static class CharacterLibrary
    {
        private static bool _loaded;
        private static readonly Dictionary<CharacterRole, List<GameObject>> Models = new Dictionary<CharacterRole, List<GameObject>>();
        private static readonly List<GameObject> AnyModels = new List<GameObject>();
        private static readonly List<AnimationClip> Clips = new List<AnimationClip>();

        private static string Folder(CharacterRole role)
        {
            switch (role)
            {
                case CharacterRole.Vampire: return "Vampires";
                case CharacterRole.Hunter: return "Hunters";
                default: return "Mortals";
            }
        }

        private static bool IsHumanoid(GameObject go)
        {
            Animator a = go != null ? go.GetComponentInChildren<Animator>() : null;
            return a != null && a.avatar != null && a.avatar.isHuman;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            foreach (CharacterRole role in new[] { CharacterRole.Vampire, CharacterRole.Mortal, CharacterRole.Hunter })
            {
                var list = new List<GameObject>();
                foreach (GameObject go in Resources.LoadAll<GameObject>("Characters/" + Folder(role)))
                    if (IsHumanoid(go)) list.Add(go);
                Models[role] = list;
            }
            foreach (GameObject go in Resources.LoadAll<GameObject>("Characters/Any"))
                if (IsHumanoid(go)) AnyModels.Add(go);
            foreach (AnimationClip c in Resources.LoadAll<AnimationClip>("Animations"))
                if (!c.name.StartsWith("__preview__")) Clips.Add(c);
        }

        public static GameObject PickModel(CharacterRole role, int seed)
        {
            Load();
            List<GameObject> list = Models[role].Count > 0 ? Models[role] : AnyModels;
            if (list.Count == 0) return null;
            return list[(seed & 0x7fffffff) % list.Count];
        }

        /// <summary>First clip whose name contains one of the keywords (checked in order).</summary>
        public static AnimationClip Clip(params string[] keywords)
        {
            Load();
            foreach (string k in keywords)
                foreach (AnimationClip c in Clips)
                    if (c.name.ToLowerInvariant().Contains(k)) return c;
            return null;
        }

        public static bool HasLocomotion
        {
            get { return Clip("idle") != null && Clip("walk") != null; }
        }
    }

    /// <summary>
    /// The visible body of a character. Uses a rigged humanoid with blended clips (Playables API, no
    /// animator controller asset needed) when one is available, otherwise an articulated procedural figure.
    /// </summary>
    public sealed class CharacterVisual : MonoBehaviour
    {
        public Transform Head { get; private set; }
        public Transform RightHand { get; private set; }

        private ProceduralFigure _figure;
        private Animator _animator;
        private Transform _model;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private readonly List<AnimationClipPlayable> _playables = new List<AnimationClipPlayable>();
        private int _idle = -1, _walk = -1, _run = -1, _feed = -1, _down = -1;
        private float[] _weights;
        private float _poseLean, _poseDown;

        public static CharacterVisual Create(Transform parent, CharacterRole role, int seed, Bloodline bloodline)
        {
            var go = new GameObject("Character");
            go.transform.SetParent(parent, false);
            var cv = go.AddComponent<CharacterVisual>();
            if (!cv.TryHumanoid(role, seed))
            {
                cv._figure = ProceduralFigure.Build(go.transform, role, seed, bloodline);
                cv.Head = cv._figure.Head;
                cv.RightHand = cv._figure.RightHand;
            }
            return cv;
        }

        private bool TryHumanoid(CharacterRole role, int seed)
        {
            if (!CharacterLibrary.HasLocomotion) return false;
            GameObject prefab = CharacterLibrary.PickModel(role, seed);
            if (prefab == null) return false;

            GameObject model = Instantiate(prefab, transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            _model = model.transform;
            _animator = model.GetComponentInChildren<Animator>();
            _animator.applyRootMotion = false;
            _animator.runtimeAnimatorController = null;
            _animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            // Normalise height (some exports use centimetres).
            Renderer[] rs = model.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                Bounds b = rs[0].bounds;
                foreach (Renderer r in rs) b.Encapsulate(r.bounds);
                float h = b.size.y;
                if (h > 0.1f && (h < 1.2f || h > 2.4f)) model.transform.localScale *= 1.78f / h;
            }

            var clips = new List<AnimationClip>();
            _idle = AddClip(clips, CharacterLibrary.Clip("idle"));
            _walk = AddClip(clips, CharacterLibrary.Clip("walk"));
            _run = AddClip(clips, CharacterLibrary.Clip("run", "jog", "sprint"));
            _feed = AddClip(clips, CharacterLibrary.Clip("feed", "bite", "drink", "kneel"));
            _down = AddClip(clips, CharacterLibrary.Clip("lying", "death", "dying", "dead"));

            _graph = PlayableGraph.Create("Sanguine Character");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
            _mixer = AnimationMixerPlayable.Create(_graph, clips.Count);
            for (int i = 0; i < clips.Count; i++)
            {
                AnimationClipPlayable p = AnimationClipPlayable.Create(_graph, clips[i]);
                p.SetTime(Random.value * clips[i].length);
                _graph.Connect(p, 0, _mixer, i);
                _mixer.SetInputWeight(i, i == _idle ? 1f : 0f);
                _playables.Add(p);
            }
            output.SetSourcePlayable(_mixer);
            _graph.Play();
            _weights = new float[clips.Count];

            Head = _animator.GetBoneTransform(HumanBodyBones.Head);
            RightHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            return true;
        }

        private static int AddClip(List<AnimationClip> list, AnimationClip c)
        {
            if (c == null) return -1;
            int i = list.IndexOf(c);
            if (i >= 0) return i;
            list.Add(c);
            return list.Count - 1;
        }

        /// <summary>Called every frame with the character's ground speed (m/s) and state.</summary>
        public void SetMotion(float speed, bool feeding, bool down)
        {
            float dt = Time.deltaTime;
            if (_figure != null)
            {
                _figure.Animate(speed, feeding, down, dt);
                return;
            }
            if (!_graph.IsValid()) return;

            for (int i = 0; i < _weights.Length; i++) _weights[i] = 0f;
            if (down && _down >= 0) _weights[_down] = 1f;
            else if (feeding && _feed >= 0) _weights[_feed] = 1f;
            else
            {
                float run = _run >= 0 ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2.6f, 4.5f, speed)) : 0f;
                float move = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 1.0f, speed));
                _weights[_idle] = 1f - move;
                _weights[_walk] = move * (1f - run);
                if (_run >= 0) _weights[_run] = move * run;
                // Match foot speed: reference speeds of typical clips.
                _playables[_walk].SetSpeed(Mathf.Clamp(speed / 1.4f, 0.6f, 2.2f));
                if (_run >= 0) _playables[_run].SetSpeed(Mathf.Clamp(speed / 5.0f, 0.7f, 1.6f));
            }
            for (int i = 0; i < _weights.Length; i++)
                _mixer.SetInputWeight(i, Mathf.MoveTowards(_mixer.GetInputWeight(i), _weights[i], dt * 6f));

            // Without dedicated clips, approximate the poses by tilting the whole body.
            float lean = feeding && _feed < 0 ? 18f : 0f;
            float lie = down && _down < 0 ? 1f : 0f;
            _poseLean = Mathf.MoveTowards(_poseLean, lean, dt * 60f);
            _poseDown = Mathf.MoveTowards(_poseDown, lie, dt * 3f);
            _model.localRotation = Quaternion.Euler(_poseLean - 90f * _poseDown, 0f, 0f);
            _model.localPosition = new Vector3(0f, 0.3f * _poseDown, 0f);
        }

        private void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
