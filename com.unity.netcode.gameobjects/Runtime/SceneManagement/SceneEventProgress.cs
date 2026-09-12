using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using AsyncOperation = UnityEngine.AsyncOperation;

namespace Unity.Netcode
{
    /// <summary>
    /// Used by <see cref="NetworkSceneManager"/> to determine if a server invoked scene event has started.
    /// The returned status is stored in the <see cref="SceneEventProgress.Status"/> property.<br/>
    /// <em>Note: This was formally known as SwitchSceneProgress which contained the <see cref="AsyncOperation"/>.
    /// All <see cref="AsyncOperation"/>s are now delivered by the <see cref="NetworkSceneManager.OnSceneEvent"/> event handler
    /// via the <see cref="SceneEvent"/> parameter.</em>
    /// </summary>
    public enum SceneEventProgressStatus
    {
        /// <summary>
        /// No scene event progress status can be used to initialize a variable that will be checked over time.
        /// </summary>
        None,
        /// <summary>
        /// The scene event was successfully started.
        /// </summary>
        Started,
        /// <summary>
        /// Returned if you try to unload a scene that was not yet loaded.
        /// </summary>
        SceneNotLoaded,
        /// <summary>
        /// Returned if you try to start a new scene event before a previous one is finished.
        /// </summary>
        SceneEventInProgress,
        /// <summary>
        /// Returned if the scene name used with <see cref="NetworkSceneManager.LoadScene(string, LoadSceneMode)"/>
        /// or <see cref="NetworkSceneManager.UnloadScene(Scene)"/>is invalid.
        /// </summary>
        InvalidSceneName,
        /// <summary>
        /// Server side: Returned if the <see cref="NetworkSceneManager.VerifySceneBeforeLoading"/> delegate handler returns false
        /// (<em>i.e. scene is considered not valid/safe to load</em>).
        /// </summary>
        SceneFailedVerification,
        /// <summary>
        /// This is used for internal error notifications.<br/>
        /// If you receive this event then it is most likely due to a bug (<em>please open a GitHub issue with steps to replicate</em>).<br/>
        /// </summary>
        InternalNetcodeError,
        /// <summary>
        /// This is returned when an unload or load action is attempted and scene management is disabled
        /// </summary>
        SceneManagementNotEnabled,
        /// <summary>
        /// This is returned when a client attempts to perform a server only action
        /// </summary>
        ServerOnlyAction,
        /// <summary>
        /// This is returned when a client that is not the session owner attempts to perform a session owner only action
        /// </summary>
        SessionOwnerOnlyAction,
    }

    /// <summary>
    /// Tracks a local scene operation and, on the authority, peer progress during load or unload.
    /// </summary>
    public class SceneEventProgress
    {
        /// <summary>
        /// List of clientIds of those clients that is done loading the scene.
        /// </summary>
        internal Dictionary<ulong, bool> ClientsProcessingSceneEvent { get; } = new Dictionary<ulong, bool>();
        internal List<ulong> ClientsThatDisconnected = new List<ulong>();

        /// <summary>
        /// This is when the current scene event will have timed out
        /// </summary>
        internal float WhenSceneEventHasTimedOut;

        /// <summary>
        /// Delegate type for when the switch scene progress is completed. Either by all clients done loading the scene or by time out.
        /// </summary>
        internal delegate bool OnCompletedDelegate(SceneEventProgress sceneEventProgress);

        /// <summary>
        /// The callback invoked when the switch scene progress is completed. Either by all clients done loading the scene or by time out.
        /// </summary>
        internal OnCompletedDelegate OnComplete;

        internal Action<uint> OnSceneEventCompleted;

        /// <summary>
        /// Reports whether the deadline elapsed; aggregate finalization is guarded separately.
        /// </summary>
        internal bool HasTimedOut()
        {
            return WhenSceneEventHasTimedOut <= m_NetworkManager.RealTimeProvider.RealTimeSinceStartup;
        }

        /// <summary>
        /// The hash value generated from the full scene path
        /// </summary>
        internal uint SceneHash { get; set; }

        internal Guid Guid { get; } = Guid.NewGuid();
        internal uint SceneEventId;

        private Coroutine m_TimeOutCoroutine;
        private Func<bool> m_AsyncOperationCompletionCheck;
        private bool m_AsyncOperationCompletionSignaled;
        private bool m_SceneEventFinished;

        private bool IsLocalOperationDone => m_AsyncOperationCompletionCheck?.Invoke() ?? false;
        private bool HasLocalOperationCompleted => m_AsyncOperationCompletionSignaled && IsLocalOperationDone;

        private NetworkManager m_NetworkManager { get; }

        internal SceneEventProgressStatus Status { get; set; }

        internal SceneEventType SceneEventType { get; set; }

        internal LoadSceneMode LoadSceneMode;

        internal List<ulong> GetClientsWithStatus(bool completedSceneEvent)
        {
            var clients = new List<ulong>();
            if (completedSceneEvent)
            {
                // Include the host/session owner only after its ready local operation signals completion.
                if ((m_NetworkManager.IsHost || m_NetworkManager.LocalClient.IsSessionOwner) && HasLocalOperationCompleted)
                {
                    clients.Add(m_NetworkManager.LocalClientId);
                }

                // Add all clients that completed the scene event
                foreach (var clientStatus in ClientsProcessingSceneEvent)
                {
                    if (clientStatus.Value == completedSceneEvent)
                    {
                        clients.Add(clientStatus.Key);
                    }
                }
            }
            else
            {
                // A ready-but-unsignaled host still belongs in the incomplete list.
                if (m_NetworkManager.IsHost && !HasLocalOperationCompleted)
                {
                    clients.Add(m_NetworkManager.LocalClientId);
                }

                // If we are getting the list of clients that have not completed the
                // scene event, then add any clients that disconnected during this
                // scene event.
                clients.AddRange(ClientsThatDisconnected);
            }
            return clients;
        }

        internal SceneEventProgress(NetworkManager networkManager, SceneEventProgressStatus status = SceneEventProgressStatus.Started)
        {
            if (status == SceneEventProgressStatus.Started)
            {
                m_NetworkManager = networkManager;
                WhenSceneEventHasTimedOut = networkManager.RealTimeProvider.RealTimeSinceStartup + networkManager.NetworkConfig.LoadSceneTimeOut;
                if ((networkManager.IsServer && !networkManager.DistributedAuthorityMode) || (networkManager.DistributedAuthorityMode && networkManager.LocalClient.IsSessionOwner))
                {
                    m_NetworkManager.OnClientDisconnectCallback += OnClientDisconnectCallback;
                    // Track the clients that were connected when we started this event
                    foreach (var connectedClientId in networkManager.ConnectionManager.ConnectedClientIds)
                    {
                        // Ignore the host or session owner
                        if ((!networkManager.DistributedAuthorityMode && NetworkManager.ServerClientId == connectedClientId) || (networkManager.DistributedAuthorityMode && networkManager.CurrentSessionOwner == connectedClientId))
                        {
                            continue;
                        }
                        ClientsProcessingSceneEvent.Add(connectedClientId, false);
                    }
                    m_TimeOutCoroutine = m_NetworkManager.StartCoroutine(TimeOutSceneEventProgress());
                }
            }
            Status = status;
        }

        /// <summary>
        /// Remove the client from the clients processing the current scene event
        /// Add this client to the clients that disconnected list
        /// </summary>
        private void OnClientDisconnectCallback(ulong clientId)
        {
            if (ClientsProcessingSceneEvent.ContainsKey(clientId))
            {
                ClientsThatDisconnected.Add(clientId);
                ClientsProcessingSceneEvent.Remove(clientId);
            }
        }

        /// <summary>
        /// Coroutine that checks to see if the scene event is complete every network tick period.
        /// This will handle completing the scene event when one or more client(s) disconnect(s)
        /// during a scene event and if it does not complete within the scene loading time out period
        /// it will time out the scene event.
        /// </summary>
        internal IEnumerator TimeOutSceneEventProgress()
        {
            var waitForNetworkTick = new WaitForSeconds(1.0f / m_NetworkManager.NetworkConfig.TickRate);
            while (!HasTimedOut())
            {
                yield return waitForNetworkTick;

                TryFinishingSceneEventProgress();
            }
        }

        /// <summary>
        /// Sets the client's scene event progress to finished/true
        /// </summary>
        internal void ClientFinishedSceneEvent(ulong clientId)
        {
            if (ClientsProcessingSceneEvent.ContainsKey(clientId))
            {
                ClientsProcessingSceneEvent[clientId] = true;
                TryFinishingSceneEventProgress();
            }
        }

        /// <summary>
        /// Returns whether the SceneEventType is related to an unloading event.
        /// </summary>
        internal bool IsUnloading()
        {
            return SceneEventType is SceneEventType.Unload or SceneEventType.UnloadComplete or SceneEventType.UnloadEventCompleted;
        }

        /// <summary>
        /// Determines if the scene event has finished for both
        /// client(s) and server.
        /// </summary>
        /// <remarks>
        /// The server checks if all known clients processing this scene event
        /// have finished and then checks signaled local completion and readiness.
        /// Clients finish when their ready local operation signals completion.
        /// </remarks>
        private bool HasFinished()
        {
            // If the network session is terminated/terminating then finish tracking
            // this scene event
            if (!IsNetworkSessionActive())
            {
                return true;
            }

            // Clients skip over this
            foreach (var clientStatus in ClientsProcessingSceneEvent)
            {
                if (!clientStatus.Value)
                {
                    return false;
                }
            }

            // Queued operations may not have a provider yet; readiness can also precede its signal.
            // Neither state counts as successful local completion. The deadline may still finalize
            // aggregate progress as timed out while the actual local operation continues.
            return HasLocalOperationCompleted;
        }

        /// <summary>
        /// Sets the AsyncOperation for the scene load/unload event
        /// </summary>
        public void SetAsyncOperation(AsyncOperation asyncOperation)
        {
            if (asyncOperation == null)
            {
                throw new ArgumentNullException(nameof(asyncOperation));
            }
            var complete = GetAsyncOperationCompletionHook(() => asyncOperation.isDone);
            asyncOperation.completed += _ => complete();
        }

        /// <summary>
        /// Supplies completion for a scene operation that is not represented by a Unity AsyncOperation.
        /// Invoke the returned hook on the main thread after activation/unload finishes. Pending calls are ignored;
        /// completion and finalization are idempotent. A ready check alone must not report a failed load as success.
        /// Aggregate timeout does not cancel the local operation: a valid later signal must still finish its scene bookkeeping.
        /// </summary>
        public Action GetAsyncOperationCompletionHook(Func<bool> check)
        {
            if (check == null)
            {
                throw new ArgumentNullException(nameof(check));
            }
            m_AsyncOperationCompletionCheck = check;
            return () =>
            {
                if (m_AsyncOperationCompletionSignaled || !IsLocalOperationDone)
                {
                    return;
                }
                m_AsyncOperationCompletionSignaled = true;
                // Don't invoke the callback if the network session is disconnected
                // during a SceneEventProgress
                if (IsNetworkSessionActive())
                {
                    OnSceneEventCompleted?.Invoke(SceneEventId);
                }

                // Aggregate progress may already have timed out; its finalization guard prevents a second report.
                // Go ahead and try finishing even if the network session is terminated/terminating
                // as we might need to stop the coroutine
                TryFinishingSceneEventProgress();
            };
        }


        internal bool IsNetworkSessionActive()
        {
            return m_NetworkManager != null && m_NetworkManager.IsListening && !m_NetworkManager.ShutdownInProgress;
        }

        /// <summary>
        /// Will try to finish the current scene event in progress as long as
        /// all conditions are met.
        /// </summary>
        internal void TryFinishingSceneEventProgress()
        {
            if (m_SceneEventFinished)
            {
                return;
            }
            if (HasFinished() || HasTimedOut())
            {
                m_SceneEventFinished = true;
                // Don't attempt to finalize this scene event if we are no longer listening or a shutdown is in progress
                if (IsNetworkSessionActive())
                {
                    OnComplete?.Invoke(this);
                    m_NetworkManager.SceneManager.SceneEventProgressTracking.Remove(Guid);
                    m_NetworkManager.OnClientDisconnectCallback -= OnClientDisconnectCallback;
                }

                if (m_TimeOutCoroutine != null && m_NetworkManager != null)
                {
                    m_NetworkManager.StopCoroutine(m_TimeOutCoroutine);
                }
            }
        }
    }
}
