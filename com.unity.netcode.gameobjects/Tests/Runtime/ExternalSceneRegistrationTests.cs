using System;
using System.Collections;
using NUnit.Framework;
using Unity.Netcode.TestHelpers.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.Netcode.RuntimeTests
{
    // Focused A/C regressions. The Addressables/two-process consumer gate is separate.
    internal class ExternalSceneRegistrationTests
    {
        private GameObject m_Object;
        private NetworkManager m_Manager;
        private Scene m_ExternalScene;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetcodeIntegrationTestHelpers.IgnoreIfServiceEnviromentVariableSet();
        }

        [SetUp]
        public void SetUp()
        {
            m_Object = new GameObject(nameof(ExternalSceneRegistrationTests));
            m_Manager = m_Object.AddComponent<NetworkManager>();
            m_Manager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = m_Object.AddComponent<DummyTransport>()
            };
            Assert.IsTrue(m_Manager.StartServer());
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_Object != null)
            {
                Object.DestroyImmediate(m_Object);
            }
            if (m_ExternalScene.IsValid() && m_ExternalScene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(m_ExternalScene);
            }
            m_ExternalScene = default;
        }

        [Test]
        public void ExternalSceneResolvesByNameAndFullPathWithoutBuildEntry()
        {
            const string path = "Assets/External/AbyssalExternalRegistrationProbe.unity";
            Assert.AreEqual(-1, SceneUtility.GetBuildIndexByScenePath(path));
            var scenes = m_Manager.SceneManager;
            scenes.RegisterExternalScenes(new[] { path });
            var hash = scenes.SceneHashFromNameOrPath(path);
            Assert.AreEqual(hash, scenes.SceneHashFromNameOrPath("AbyssalExternalRegistrationProbe"));
            Assert.AreEqual(path, scenes.ScenePathFromHash(hash));
            Assert.AreEqual("AbyssalExternalRegistrationProbe", scenes.SceneNameFromHash(hash));
            Assert.AreEqual("No Scene", scenes.SceneNameFromHash(0));
        }

        [Test]
        public void AmbiguousRegistrationFailsAtomicallyAndReplacementRemovesOldNames()
        {
            var scenes = m_Manager.SceneManager;
            const string original = "Assets/External/Original.unity";
            scenes.RegisterExternalScenes(new[] { original });
            var hash = scenes.SceneHashFromNameOrPath(original);
            Assert.Throws<ArgumentException>(() => scenes.RegisterExternalScenes(new[]
            {
                "Assets/First/Duplicate.unity", "Assets/Second/Duplicate.unity"
            }));
            Assert.AreEqual(hash, scenes.SceneHashFromNameOrPath("Original"));
            scenes.RegisterExternalScenes(new[] { "Assets/External/Replacement.unity" });
            Assert.Throws<Exception>(() => scenes.SceneHashFromNameOrPath("Original"));
            Assert.DoesNotThrow(() => scenes.SceneHashFromNameOrPath("Replacement"));
        }

        [Test]
        public void ExternalNameCannotShadowABuiltInSceneAndRejectionIsAtomic()
        {
            var scenes = m_Manager.SceneManager;
            var builtInPath = SceneUtility.GetScenePathByBuildIndex(0);
            Assert.IsNotEmpty(builtInPath, "The networking fixture must have its bootstrap scene in Build Settings.");
            var builtInHash = scenes.SceneHashFromNameOrPath(builtInPath);
            const string original = "Assets/External/Original.unity";
            scenes.RegisterExternalScenes(new[] { original });
            var originalHash = scenes.SceneHashFromNameOrPath(original);
            var conflictingPath = "Assets/External/CollisionProbe/" + System.IO.Path.GetFileName(builtInPath);
            Assert.AreNotEqual(builtInPath, conflictingPath);
            Assert.Throws<ArgumentException>(() => scenes.RegisterExternalScenes(new[] { conflictingPath }));
            Assert.AreEqual(originalHash, scenes.SceneHashFromNameOrPath(original));
            Assert.AreEqual(builtInHash, scenes.SceneHashFromNameOrPath(builtInPath));
            Assert.AreEqual(builtInHash, scenes.SceneHashFromNameOrPath(System.IO.Path.GetFileNameWithoutExtension(builtInPath)));
        }

        [Test]
        public void ExternalRegistrationPreservesTheValidationVeto()
        {
            var scenes = m_Manager.SceneManager;
            scenes.RegisterExternalScenes(new[] { "Assets/External/Vetoed.unity" });
            scenes.DisableValidationWarnings(true);
            var calls = 0;
            scenes.VerifySceneBeforeLoading = (index, name, mode) =>
            {
                Assert.AreEqual(-1, index);
                Assert.AreEqual("Vetoed", name);
                calls++;
                return false;
            };
            Assert.AreEqual(SceneEventProgressStatus.SceneFailedVerification,
                scenes.LoadScene("Vetoed", LoadSceneMode.Additive));
            Assert.AreEqual(1, calls);
        }

        [UnityTest]
        public IEnumerator LoadedExternalSceneCanBeResolvedForActiveSceneSynchronization()
        {
            m_ExternalScene = SceneManager.CreateScene("AbyssalExternalActiveProbe");
            var scenes = m_Manager.SceneManager;
            scenes.RegisterExternalScenes(new[] { "Assets/External/AbyssalExternalActiveProbe.unity" });
            Assert.IsTrue(scenes.TryGetSceneHash(m_ExternalScene, out var hash));
            Assert.IsTrue(scenes.TryGetLoadedSceneFromHash(hash, out var resolved));
            Assert.AreEqual(m_ExternalScene, resolved);
            Assert.IsFalse(scenes.TryGetLoadedSceneFromHash(0, out _));
            yield return null;
        }

        [Test]
        public void ManagedCompletionWaitsForSignalAndCompletesExactlyOnce()
        {
            var progress = new SceneEventProgress(m_Manager);
            var callbacks = 0;
            var finalizations = 0;
            progress.OnSceneEventCompleted = _ => callbacks++;
            progress.OnComplete = _ => { finalizations++; return true; };
            var ready = false;
            var complete = progress.GetAsyncOperationCompletionHook(() => ready);
            complete();
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(0, callbacks);
            Assert.AreEqual(0, finalizations);
            ready = true;
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(0, finalizations, "Readiness must not outrun the provider's completion callback.");
            complete();
            complete();
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(1, callbacks);
            Assert.AreEqual(1, finalizations);
        }

        [Test]
        public void AggregateTimeoutStillAllowsLocalCompletionWithoutFinalizingTwice()
        {
            var progress = new SceneEventProgress(m_Manager);
            var callbacks = 0;
            var finalizations = 0;
            progress.OnSceneEventCompleted = _ => callbacks++;
            progress.OnComplete = _ => { finalizations++; return true; };
            var ready = false;
            var complete = progress.GetAsyncOperationCompletionHook(() => ready);
            progress.WhenSceneEventHasTimedOut = float.NegativeInfinity;
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(0, callbacks);
            Assert.AreEqual(1, finalizations);
            ready = true;
            complete();
            complete();
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(1, callbacks, "The completed local operation must finish scene bookkeeping after aggregate timeout.");
            Assert.AreEqual(1, finalizations);
        }

        [UnityTest]
        public IEnumerator ReadyButUnsignaledHostIsReportedIncompleteAtTimeout()
        {
            m_Manager.Shutdown();
            var shutdownDeadline = Time.realtimeSinceStartup + 5f;
            while (m_Manager.IsListening || m_Manager.ShutdownInProgress)
            {
                Assert.Less(Time.realtimeSinceStartup, shutdownDeadline, "Server shutdown did not finish before the host regression.");
                yield return null;
            }
            Assert.IsTrue(m_Manager.StartHost());
            var progress = new SceneEventProgress(m_Manager);
            var ready = false;
            var complete = progress.GetAsyncOperationCompletionHook(() => ready);
            Assert.IsFalse(progress.GetClientsWithStatus(true).Contains(m_Manager.LocalClientId));
            Assert.IsTrue(progress.GetClientsWithStatus(false).Contains(m_Manager.LocalClientId));
            ready = true;
            Assert.IsFalse(progress.GetClientsWithStatus(true).Contains(m_Manager.LocalClientId));
            Assert.IsTrue(progress.GetClientsWithStatus(false).Contains(m_Manager.LocalClientId));
            var finalizations = 0;
            progress.OnComplete = completed =>
            {
                finalizations++;
                Assert.IsFalse(completed.GetClientsWithStatus(true).Contains(m_Manager.LocalClientId));
                Assert.IsTrue(completed.GetClientsWithStatus(false).Contains(m_Manager.LocalClientId));
                return true;
            };
            progress.WhenSceneEventHasTimedOut = float.NegativeInfinity;
            progress.TryFinishingSceneEventProgress();
            Assert.AreEqual(1, finalizations);
            complete();
            Assert.AreEqual(1, finalizations);
            Assert.IsTrue(progress.GetClientsWithStatus(true).Contains(m_Manager.LocalClientId));
            Assert.IsFalse(progress.GetClientsWithStatus(false).Contains(m_Manager.LocalClientId));
        }

        [Test]
        public void ManagedCompletionAfterShutdownDoesNotNotifySceneSuccess()
        {
            var progress = new SceneEventProgress(m_Manager);
            var callbacks = 0;
            progress.OnSceneEventCompleted = _ => callbacks++;
            var complete = progress.GetAsyncOperationCompletionHook(() => true);
            m_Manager.Shutdown();
            complete();
            Assert.AreEqual(0, callbacks);
        }
    }
}
