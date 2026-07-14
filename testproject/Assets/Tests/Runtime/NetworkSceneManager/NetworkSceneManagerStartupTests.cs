using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.TestHelpers.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TestProject.RuntimeTests
{
    internal class NetworkSceneManagerStartupTests : NetcodeIntegrationTest
    {
        private const string k_InitialScene = "SessionSynchronize";
        private const string k_ActiveScene = "InSceneNetworkObject";
        private Scene m_OriginalActiveScene;
        protected override int NumberOfClients => 0;

        private readonly List<NetworkObject> m_ObjectsInScenes = new List<NetworkObject>();
        private Scene m_SceneLoaded;

        private bool m_CanStart = false;
        protected override bool CanStartServerAndClients() => m_CanStart;

        protected override IEnumerator OnSetup()
        {
            m_OriginalActiveScene = SceneManager.GetActiveScene();
            return base.OnSetup();
        }

        private GameObject m_RandomObjectPrefab;

        protected override void OnServerAndClientsCreated()
        {
            m_RandomObjectPrefab = CreateNetworkObjectPrefab("randomObject");
            m_RandomObjectPrefab.AddComponent<RpcSender>();
            // m_RandomObjectPrefab.GetComponent<NetworkObject>().AutoSpawnOnStart = false;
            base.OnServerAndClientsCreated();
        }

        private IEnumerator PreLoadScene(string sceneName)
        {
            SceneManager.sceneLoaded += OnSceneLoad;
            SceneManager.LoadScene(sceneName, LoadSceneMode.Additive);
            yield return WaitForConditionOrTimeOut(() => m_SceneLoaded.IsValid());
            AssertOnTimeout("Timed out waiting for scene to load!");
            SceneManager.sceneLoaded -= OnSceneLoad;
        }

        [UnityTest]
        public IEnumerator AllExistingObjectsAreSpawnedAtStartup()
        {
            yield return PreLoadScene(k_InitialScene);
            var otherScene = m_SceneLoaded;
            yield return PreLoadScene(k_ActiveScene);
            SceneManager.SetActiveScene(m_SceneLoaded);

            var existingObjects = new List<NetworkObject>();
            var dontDestroyOnLoadCount = 0;
            foreach (var obj in m_ObjectsInScenes)
            {
                Assert.IsFalse(obj.IsSpawned, $"NetworkObject {obj.name} should not have been spawned!");

                existingObjects.Add(obj);
                if (obj.gameObject.scene.name == k_ActiveScene)
                {
                    dontDestroyOnLoadCount++;
                    SceneManager.MoveGameObjectToScene(obj.gameObject, otherScene);
                    Object.DontDestroyOnLoad(obj.gameObject);
                }
            }

            var randomObject1 = Object.Instantiate(m_RandomObjectPrefab);
            var randomObject2 = Object.Instantiate(m_RandomObjectPrefab);

            var authority = GetAuthorityNetworkManager();
            existingObjects.Add(randomObject1.GetComponent<NetworkObject>());
            existingObjects.Add(randomObject2.GetComponent<NetworkObject>());

            Assert.IsNotEmpty(existingObjects, "Expected to have some existing objects!");
            Assert.That(dontDestroyOnLoadCount, Is.GreaterThan(0), "Expected to have some objects moved to DontDestroyOnLoad!");

            authority.OnClientConnectedCallback -= OnClientConnected;
            authority.OnClientConnectedCallback += OnClientConnected;

            m_CanStart = true;
            yield return StartServerAndClients();

            foreach (var existingObject in existingObjects)
            {
                Assert.IsFalse(existingObject == null, "Expected existing object to still exist!");
                Assert.IsTrue(existingObject.IsSpawned, $"NetworkObject {existingObject.name} in scene {existingObject.gameObject.scene.name} was not spawned!");
                // Assert.IsTrue(existingObject.InScenePlaced, $"NetworkObject {existingObject.name} in scene {existingObject.gameObject.scene.name} was not inScenePlaced!");
                // Assert.IsTrue(existingObject.IsSceneObject == true, $"NetworkObject {existingObject.name} in scene {existingObject.gameObject.scene.name} was not inScenePlaced!");
            }

            // Any scenes that we load through SceneManagement won't be associated with a NetworkManager
            // When the next NetworkManager is created and started these scenes will be assigned to that NetworkManager
            m_ObjectsInScenes.Clear();
            yield return PreLoadScene(k_InitialScene);
            yield return PreLoadScene(k_ActiveScene);
            SceneManager.SetActiveScene(m_SceneLoaded);

            var objectsToCheck = new List<NetworkObject>();
            foreach (var obj in m_ObjectsInScenes)
            {
                Assert.IsFalse(obj.IsSpawned, $"NetworkObject {obj.name} should not have been spawned!");

                objectsToCheck.Add(obj);
                if (obj.gameObject.scene.name == k_ActiveScene)
                {
                    dontDestroyOnLoadCount++;
                    Object.DontDestroyOnLoad(obj.gameObject);
                }
            }

            var clientSideObject = Object.Instantiate(m_RandomObjectPrefab);
            clientSideObject.GetComponent<RpcSender>().OwnerId = 1;
            objectsToCheck.Add(clientSideObject.GetComponent<NetworkObject>());
            var networkObj =  clientSideObject.GetComponent<NetworkObject>();
            Debug.Log($"Client side object is {clientSideObject.name}. IsSpawned: {networkObj.IsSpawned}, hasBeenSpawned: {networkObj.HasBeenSpawned}");
            Assert.That(dontDestroyOnLoadCount, Is.GreaterThan(0), "Expected to have some objects moved to DontDestroyOnLoad!");

            yield return CreateAndStartNewClient();

            yield return WaitForSpawnedOnAllOrTimeOut(existingObjects);
            AssertOnTimeout("Timed out waiting for objects to spawn on all clients!");

            foreach (var networkObject in objectsToCheck)
            {
                Assert.IsTrue(networkObject.IsSpawned, "Object preloaded from client scene should have been spawned!");
            }
        }

        private void TrackObjectsInScene(Scene scene)
        {
            foreach (var rootObject in scene.GetRootGameObjects())
            {
                foreach (var networkObject in rootObject.GetComponentsInChildren<NetworkObject>())
                {
                    m_ObjectsInScenes.Add(networkObject);
                }
            }
        }

        private void OnSceneLoad(Scene scene, LoadSceneMode loadSceneMode)
        {
            m_SceneLoaded = scene;
            TrackObjectsInScene(scene);
        }

        protected override IEnumerator OnTearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            m_ObjectsInScenes.Clear();
            SceneManager.SetActiveScene(m_OriginalActiveScene);
            yield return base.OnTearDown();
        }

        private void OnClientConnected(ulong clientId)
        {
            var allSenders = FindObjects.ByType<RpcSender>();
            var rpcSender = allSenders.FirstOrDefault((obj) => obj.OwnerId == clientId && obj.IsSpawned);
            if (rpcSender == null)
            {
                foreach (var sender in allSenders)
                {
                    Debug.Log($"Found Sender: {sender.name} ({sender.GetInstanceID()}), OwnerId: {sender.OwnerId}, IsSpawned: {sender.IsSpawned}");
                }
                Assert.Fail($"Expected to find RpcSender instance for client-{clientId}!");
                return;
            }
#pragma warning disable CS0618 // Type or member is obsolete
            Debug.Log($"RpcSender InScenePlaced: {rpcSender.NetworkObject.IsSceneObject}. clientId: {clientId}");
#pragma warning restore CS0618 // Type or member is obsolete
            rpcSender.ClientsAndHostClientRpc(clientId);
        }
    }

    internal class RpcSender : NetworkBehaviour
    {
        private ulong m_TempOwner;

        public ulong OwnerId
        {
            get => m_TempOwner;
            set
            {
                Debug.Log($"[{name}][{IsSpawned}][{NetworkManager?.LocalClientId}] Setting ownerId to " + value);
                m_TempOwner = value;
            }
        }
        [Rpc(SendTo.ClientsAndHost)]
        public void ClientsAndHostClientRpc(ulong clientId)
        {
            Debug.Log($"ClientsAndHostClientRpc called! clientId: {clientId}");
        }
    }
}
