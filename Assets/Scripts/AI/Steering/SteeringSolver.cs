// -----------------------------------------------------------------------------
// Vigil — local steering.
//
// The path corridor says WHERE to go. Steering decides how to get there without
// scraping walls, stacking on other agents, or accelerating instantaneously.
//
// Entirely allocation-free: fixed neighbour buffer, NonAlloc physics queries,
// explicit layer masks. This runs inside the agent tick, which is budgeted, so a
// single GC allocation here is multiplied by agent count and tick rate.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Simulation;

namespace Vigil.AI.Steering
{
    public struct SteeringSettings
    {
        public float MaxSpeed;
        public float MaxAcceleration;

        /// <summary>Radius within which other agents push this one away.</summary>
        public float SeparationRadius;
        public float SeparationWeight;

        /// <summary>Distance ahead probed for obstacles.</summary>
        public float AvoidanceProbeDistance;
        public float AvoidanceWeight;

        public float AgentRadius;

        public LayerMask ObstacleMask;
        public LayerMask AgentMask;

        public static SteeringSettings Default => new SteeringSettings
        {
            MaxSpeed = 4f,
            MaxAcceleration = 12f,
            SeparationRadius = 1.6f,
            SeparationWeight = 1.4f,
            AvoidanceProbeDistance = 2.2f,
            AvoidanceWeight = 1.8f,
            AgentRadius = 0.45f,
            ObstacleMask = ~0,
            AgentMask = 0
        };
    }

    public sealed class SteeringSolver
    {
        const int MaxNeighbours = 8;

        readonly Collider[] _neighbourBuffer = new Collider[MaxNeighbours];
        readonly RaycastHit[] _probeBuffer = new RaycastHit[4];

        float3 _currentVelocity;

        /// <summary>Velocity produced on the most recent solve.</summary>
        public float3 CurrentVelocity => _currentVelocity;

        public void Reset() => _currentVelocity = float3.zero;

        /// <summary>
        /// Blends the path-follower's desired velocity with separation and obstacle
        /// avoidance, then applies an acceleration limit.
        /// </summary>
        public float3 Solve(
            float3 position,
            float3 desiredVelocity,
            in SteeringSettings settings,
            Transform self,
            in SimTime time)
        {
            float3 steering = desiredVelocity;

            if (settings.SeparationWeight > 0f && settings.AgentMask.value != 0)
            {
                steering += Separation(position, in settings, self) * settings.SeparationWeight;
            }

            if (settings.AvoidanceWeight > 0f && math.lengthsq(desiredVelocity) > 1e-4f)
            {
                steering += Avoidance(position, desiredVelocity, in settings) * settings.AvoidanceWeight;
            }

            // Clamp to max speed before the acceleration limit, so blending cannot
            // produce a vector that is briefly faster than the agent can ever move.
            float speedSq = math.lengthsq(steering);
            if (speedSq > settings.MaxSpeed * settings.MaxSpeed)
            {
                steering = math.normalize(steering) * settings.MaxSpeed;
            }

            // Acceleration limit. Without it, a corridor corner produces an instant
            // direction reversal that reads as teleporting.
            float3 delta = steering - _currentVelocity;
            float maxDelta = settings.MaxAcceleration * time.DeltaTime;
            float deltaLenSq = math.lengthsq(delta);

            if (deltaLenSq > maxDelta * maxDelta)
            {
                delta = math.normalize(delta) * maxDelta;
            }

            _currentVelocity += delta;
            _currentVelocity.y = 0f;

            return _currentVelocity;
        }

        float3 Separation(float3 position, in SteeringSettings settings, Transform self)
        {
            int count = Physics.OverlapSphereNonAlloc(
                position, settings.SeparationRadius, _neighbourBuffer,
                settings.AgentMask, QueryTriggerInteraction.Ignore);

            if (count == 0) return float3.zero;

            float3 push = float3.zero;
            int contributors = 0;

            for (int i = 0; i < count; i++)
            {
                Collider c = _neighbourBuffer[i];
                if (c == null) continue;
                if (self != null && c.transform == self) continue;
                if (self != null && c.transform.IsChildOf(self)) continue;

                float3 away = position - (float3)c.transform.position;
                away.y = 0f;

                float distSq = math.lengthsq(away);
                if (distSq < 1e-4f) continue;

                // Inverse-distance weighting: close neighbours push much harder, so
                // agents separate decisively rather than drifting apart.
                float dist = math.sqrt(distSq);
                push += (away / dist) * (1f - math.saturate(dist / settings.SeparationRadius));
                contributors++;
            }

            return contributors > 0 ? push / contributors : float3.zero;
        }

        float3 Avoidance(float3 position, float3 desiredVelocity, in SteeringSettings settings)
        {
            float3 dir = math.normalize(desiredVelocity);
            Vector3 origin = (Vector3)position + Vector3.up * 0.9f;

            int hits = Physics.SphereCastNonAlloc(
                origin, settings.AgentRadius, (Vector3)dir, _probeBuffer,
                settings.AvoidanceProbeDistance, settings.ObstacleMask, QueryTriggerInteraction.Ignore);

            if (hits == 0) return float3.zero;

            // Nearest blocker only. Averaging every hit produces a mush that steers
            // into corners rather than around them.
            int nearest = 0;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < hits; i++)
            {
                if (_probeBuffer[i].distance < nearestDist)
                {
                    nearestDist = _probeBuffer[i].distance;
                    nearest = i;
                }
            }

            float3 normal = _probeBuffer[nearest].normal;
            normal.y = 0f;

            if (math.lengthsq(normal) < 1e-4f) return float3.zero;

            normal = math.normalize(normal);

            // Slide along the surface rather than bouncing off it: project the
            // desired direction onto the wall plane and blend by how close we are.
            float3 slide = dir - normal * math.dot(dir, normal);
            float urgency = 1f - math.saturate(nearestDist / math.max(0.01f, settings.AvoidanceProbeDistance));

            return (slide + normal * 0.35f) * settings.MaxSpeed * urgency;
        }
    }
}
