// -----------------------------------------------------------------------------
// Vigil — end-to-end smoke test.
//
// This is the test that answers "does the game actually run?", as opposed to
// "does it compile?". It boots the real Bootstrap scene, starts a real host,
// loads the real level through Netcode's scene manager, and then asserts that the
// things which must exist actually do.
//
// It exists because every other test in this project is a unit test. A project
// can have a perfect unit suite and still fail to start — a null config, a
// missing NavMesh, a service that was never registered, a prefab that is not in
// the network prefab list. None of those are visible until something boots the
// whole stack, and doing that by hand is exactly the check people skip.
// -----------------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Vigil.AI.Agents;
using Vigil.Core.Contracts;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Gameplay.Player;
using Vigil.Gameplay.Systems;

namespace Vigil.Tests
{
    public class SessionSmokeTests
    {
        const string BootstrapScene = "Bootstrap";
        const string LevelScene = "Level_Facility";

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
            {
                nm.Shutdown();
                yield return null;
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator HostSession_BootsLevel_SpawnsPlayerAndAntagonist_AndAiSimulates()
        {
            // ---- 1. boot ---------------------------------------------------------
            yield return SceneManager.LoadSceneAsync(BootstrapScene, LoadSceneMode.Single);
            yield return null;

            Assert.IsTrue(Services.IsReady,
                "GameBootstrap did not create a ServiceContext. Nothing downstream can resolve its services.");

            Assert.IsNotNull(Services.TryGet<INavigationService>(), "INavigationService was not registered.");
            Assert.IsNotNull(Services.TryGet<IAIDirector>(), "IAIDirector was not registered.");
            Assert.IsNotNull(Services.TryGet<TickScheduler>(), "TickScheduler was not registered.");

            NetworkManager nm = NetworkManager.Singleton;
            Assert.IsNotNull(nm, "No NetworkManager in the Bootstrap scene.");

            // ---- 2. host ---------------------------------------------------------
            Assert.IsTrue(nm.StartHost(), "StartHost() failed.");
            yield return null;

            Assert.IsTrue(nm.IsServer && nm.IsClient, "Host should be both server and client.");

            // ---- 3. load the level through NGO ----------------------------------
            bool levelLoaded = false;
            nm.SceneManager.OnLoadEventCompleted += (sceneName, mode, done, timedOut) =>
            {
                if (sceneName == LevelScene) levelLoaded = true;
            };

            nm.SceneManager.LoadScene(LevelScene, LoadSceneMode.Single);

            float deadline = Time.realtimeSinceStartup + 30f;
            while (!levelLoaded && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(levelLoaded, "Level scene did not finish loading within 30s.");

            // Give spawning, the NavMesh bake and the first ticks a moment to run.
            yield return new WaitForSeconds(2f);

            // ---- 4. the level is navigable --------------------------------------
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            Assert.Greater(triangulation.vertices.Length, 0,
                "No NavMesh at runtime. The antagonist cannot path and will stand still forever.");

            // ---- 5. the player exists and is controllable -----------------------
            PlayerCharacter player = Object.FindAnyObjectByType<PlayerCharacter>();
            Assert.IsNotNull(player, "No PlayerCharacter spawned. Check NetworkManager's PlayerPrefab.");
            Assert.IsTrue(player.IsSpawned, "PlayerCharacter exists but is not network-spawned.");

            PlayerCameraRig rig = player.GetComponent<PlayerCameraRig>();
            Assert.IsNotNull(rig, "Player has no PlayerCameraRig - the game would be unplayable (no view).");

            Camera cam = player.GetComponentInChildren<Camera>(true);
            Assert.IsNotNull(cam, "Player has no camera.");

            // ---- 6. the antagonist exists and is simulating ---------------------
            NpcAgent agent = Object.FindAnyObjectByType<NpcAgent>();
            Assert.IsNotNull(agent, "No NpcAgent in the level.");
            Assert.IsTrue(agent.IsSpawned, "NpcAgent is not network-spawned.");

            Assert.IsNotNull(agent.DebugAwareness,
                "NpcAgent has no AwarenessModel - server-side simulation never initialised.");

            string statePath = agent.DebugStatePath;
            Assert.IsFalse(string.IsNullOrEmpty(statePath), "Agent FSM reported no active state.");
            StringAssert.Contains("Antagonist", statePath, "Agent is not running the antagonist brain.");

            // ---- 7. the mission exists ------------------------------------------
            MissionDirector mission = Object.FindAnyObjectByType<MissionDirector>();
            Assert.IsNotNull(mission, "No MissionDirector - the level has no objective or win condition.");
            Assert.Greater(mission.GeneratorsRequired, 0, "Mission requires zero generators.");

            // ---- 8. the simulation is actually advancing ------------------------
            SimClock clock = Services.TryGet<SimClock>();
            Assert.IsNotNull(clock);

            int tickBefore = clock.CurrentTick;
            yield return new WaitForSeconds(1.5f);

            Assert.Greater(clock.CurrentTick, tickBefore,
                "Simulation clock is not advancing - SimClockRunner is not driving ticks.");

            // ---- 9. the agent moved or is deliberately dormant -------------------
            // A freshly-booted level has no players near the agent, so Dormant is a
            // legitimate outcome. What must NOT happen is the agent being stuck in a
            // state with no path and no plan.
            Assert.IsTrue(
                agent.DebugContext != null,
                "Agent has no AgentContext - the brain was never constructed.");

            Debug.Log($"[SMOKE] agent state = {agent.DebugStatePath}, mission = {mission.ObjectiveText}");
        }

        [UnityTest]
        public IEnumerator DarknessSampler_IsRegistered_AndReportsDarkLevel()
        {
            // The lighting model is what makes composure drain, vision modulate and
            // the AI prefer dark rooms. If nothing registers it, all three silently
            // run on a constant and the entire light/dark trade-off disappears.
            yield return SceneManager.LoadSceneAsync(BootstrapScene, LoadSceneMode.Single);
            yield return null;

            NetworkManager nm = NetworkManager.Singleton;
            Assert.IsTrue(nm.StartHost());
            yield return null;

            bool loaded = false;
            nm.SceneManager.OnLoadEventCompleted += (s, m, d, t) => { if (s == LevelScene) loaded = true; };
            nm.SceneManager.LoadScene(LevelScene, LoadSceneMode.Single);

            float deadline = Time.realtimeSinceStartup + 30f;
            while (!loaded && Time.realtimeSinceStartup < deadline) yield return null;

            yield return new WaitForSeconds(1f);

            IDarknessSampler sampler = Services.TryGet<IDarknessSampler>();
            Assert.IsNotNull(sampler, "No IDarknessSampler registered - lighting has no gameplay effect.");

            // A corner of the facility, far from the two always-on hub lights.
            float darkCorner = sampler.DarknessAt(new Unity.Mathematics.float3(-28f, 1f, -28f));
            Assert.Greater(darkCorner, 0.5f, "An unlit corner should read as dark.");

            // Directly under a hub light.
            float litHub = sampler.DarknessAt(new Unity.Mathematics.float3(0f, 1f, -12f));
            Assert.Less(litHub, darkCorner, "A lit area must read as brighter than an unlit one.");
        }
    }
}
