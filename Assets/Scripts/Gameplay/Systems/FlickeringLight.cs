// -----------------------------------------------------------------------------
// Vigil — failing practical lights.
//
// A steady light is furniture; a failing one is a threat. Flicker does three jobs
// at once: it animates an otherwise static level, it makes the same corridor read
// differently on each pass, and it briefly removes the light the player is
// relying on — which is the cheapest way to make a safe space stop feeling safe.
//
// Driven by DeterministicRandom rather than UnityEngine.Random so a given fixture
// behaves identically across a reload, and so it never perturbs the simulation's
// random streams.
// -----------------------------------------------------------------------------

using UnityEngine;
using Vigil.Core.Mathx;

namespace Vigil.Gameplay.Systems
{
    [RequireComponent(typeof(Light))]
    public sealed class FlickeringLight : MonoBehaviour
    {
        [SerializeField, Min(0f), Tooltip("Baseline intensity. Captured from the Light on Awake if left at 0.")]
        float _baseIntensity = 0f;

        [SerializeField, Range(0f, 1f), Tooltip("How far the intensity can dip. 1 allows a full blackout.")]
        float _depth = 0.75f;

        [SerializeField, Min(0.01f), Tooltip("Seconds between flicker events. Lower is more agitated.")]
        float _interval = 2.4f;

        [SerializeField, Range(0f, 1f), Tooltip("Chance a flicker becomes a full dropout rather than a dip.")]
        float _dropoutChance = 0.22f;

        Light _light;
        DeterministicRandom _rng;

        float _timer;
        float _current = 1f;
        float _target = 1f;
        float _dropoutRemaining;

        void Awake()
        {
            _light = GetComponent<Light>();
            if (_baseIntensity <= 0f) _baseIntensity = _light.intensity;

            // Seeded from the object's position so each fixture in the level has its
            // own rhythm without any authoring, and the same fixture is stable.
            Vector3 p = transform.position;
            uint seed = (uint)(Mathf.RoundToInt(p.x * 31f) * 73856093 ^ Mathf.RoundToInt(p.z * 31f) * 19349663);
            _rng = new DeterministicRandom(seed == 0u ? 1u : seed);

            _timer = _rng.NextFloat(0f, _interval);
        }

        void Update()
        {
            float dt = Time.deltaTime;

            if (_dropoutRemaining > 0f)
            {
                _dropoutRemaining -= dt;
                _light.intensity = 0f;
                if (_dropoutRemaining <= 0f) _target = 1f;
                return;
            }

            _timer -= dt;

            if (_timer <= 0f)
            {
                _timer = _rng.NextFloat(_interval * 0.35f, _interval * 1.65f);

                if (_rng.Chance(_dropoutChance))
                {
                    // A full dropout is the payload. Short, so it reads as a fault
                    // rather than as the level simply being unlit.
                    _dropoutRemaining = _rng.NextFloat(0.08f, 0.45f);
                    return;
                }

                _target = 1f - _rng.NextFloat(0f, _depth);
            }

            // Snap down, ease back up: a failing filament dies fast and recovers
            // slowly. Symmetric interpolation reads as a pulsing disco light.
            float rate = _target < _current ? 26f : 6f;
            _current = Mathf.MoveTowards(_current, _target, rate * dt);

            if (Mathf.Abs(_current - _target) < 0.01f && _target < 1f) _target = 1f;

            _light.intensity = _baseIntensity * _current;
        }
    }
}
