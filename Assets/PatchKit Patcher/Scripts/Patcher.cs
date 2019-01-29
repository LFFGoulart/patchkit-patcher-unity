﻿using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PatchKit.Api;
using PatchKit.Apps;
using PatchKit.Apps.Updating;
using PatchKit.Apps.Updating.AppUpdater.Status;
using PatchKit.Apps.Updating.Debug;
using PatchKit.Apps.Updating.Utilities;
using PatchKit.Network;
using PatchKit.Patching.Unity.UI.Dialogs;
using UniRx;
using UnityEngine;
using CancellationToken = System.Threading.CancellationToken;

namespace PatchKit.Patching.Unity
{
    // Assumptions:
    // - this component is always enabled (coroutines are always executed)
    // - this component is destroyed only when application quits
    public partial class Patcher : MonoBehaviour
    {
        public const string EditorAllowedSecret = "ac20fc855b75a7ea5f3e936dfd38ccd8";

        public enum UserDecision
        {
            None,
            StartApp,
            StartAppAutomatically,
            InstallApp,
            InstallAppAutomatically,
            CheckForAppUpdates,
            CheckForAppUpdatesAutomatically,
            CheckIntegrity,
            CheckIntegrityAutomatically,
        }

        private static Patcher _instance;

        public static Patcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<Patcher>();
                }
                return _instance;
            }
        }

        private bool _canStartThread = true;

        private readonly CancellationTokenSource _threadCancellationTokenSource = new CancellationTokenSource();

        public CancellationToken ThreadCancellationToken => _threadCancellationTokenSource.Token;

        private Thread _thread;

        private bool _isThreadBeingKilled;

        private App _app;

        private RemoteApp _remoteApp;

        private PatcherConfiguration _configuration;

        private UserDecision _userDecision = UserDecision.None;

        private readonly ManualResetEvent _userDecisionSetEvent = new ManualResetEvent(false);
        
        private bool _hasAutomaticallyInstalledApp;

        private bool _hasAutomaticallyCheckedForAppUpdate;

        private bool _hasAutomaticallyCheckVersionIntegrity;

        private bool _hasAutomaticallyStartedApp;

        private FileStream _lockFileStream;

        private CancellationTokenSource _updateAppCancellationTokenSource;

        public ErrorDialog ErrorDialog;

        public string EditorAppSecret;

        public int EditorOverrideLatestVersionId;

        public PatcherConfiguration DefaultConfiguration;
        
        #region ReactiveProperties
        private readonly ReactiveProperty<IReadOnlyUpdaterStatus> _updaterStatus = new ReactiveProperty<IReadOnlyUpdaterStatus>();

        public IReadOnlyReactiveProperty<IReadOnlyUpdaterStatus> UpdaterStatus => _updaterStatus;

        private readonly BoolReactiveProperty _canRepairApp = new BoolReactiveProperty(false);

        public IReadOnlyReactiveProperty<bool> CanRepairApp => _canRepairApp;

        private readonly BoolReactiveProperty _canStartApp = new BoolReactiveProperty(false);

        public IReadOnlyReactiveProperty<bool> CanStartApp => _canStartApp;

        private readonly BoolReactiveProperty _canInstallApp = new BoolReactiveProperty(false);

        public IReadOnlyReactiveProperty<bool> CanInstallApp => _canInstallApp;

        private readonly BoolReactiveProperty _canCheckForAppUpdates = new BoolReactiveProperty(false);

        public IReadOnlyReactiveProperty<bool> CanCheckForAppUpdates => _canCheckForAppUpdates;

        private readonly ReactiveProperty<PatcherState> _state = new ReactiveProperty<PatcherState>(PatcherState.None);

        public IReadOnlyReactiveProperty<PatcherState> State => _state;

        private readonly ReactiveProperty<PatcherData> _data = new ReactiveProperty<PatcherData>();

        public IReadOnlyReactiveProperty<PatcherData> Data => _data;

        private readonly ReactiveProperty<string> _warning = new ReactiveProperty<string>();

        public IReadOnlyReactiveProperty<string> Warning => _warning;

        private readonly ReactiveProperty<int?> _remoteVersionId = new ReactiveProperty<int?>();

        public IReadOnlyReactiveProperty<int?> RemoteVersionId => _remoteVersionId;

        private readonly ReactiveProperty<int?> _localVersionId = new ReactiveProperty<int?>();

        public IReadOnlyReactiveProperty<int?> LocalVersionId => _localVersionId;

        private readonly ReactiveProperty<Api.Models.App> _appInfo = new ReactiveProperty<Api.Models.App>();

        public IReadOnlyReactiveProperty<Api.Models.App> AppInfo => _appInfo;

        #endregion

        public void SetUserDecision(UserDecision userDecision)
        {
            UnityEngine.Debug.Log($"User deicision set to {userDecision}.");

            _userDecision = userDecision;
            _userDecisionSetEvent.Set();
        }

        public void CancelUpdateApp()
        {
            if (_updateAppCancellationTokenSource != null)
            {
                UnityEngine.Debug.Log("Cancelling update app execution.");

                _updateAppCancellationTokenSource.Cancel();
            }
        }

        public void Quit()
        {
            UnityEngine.Debug.Log("Quitting application.");
            _canStartThread = false;

#if UNITY_EDITOR
            if (Application.isEditor)
            {
                UnityEditor.EditorApplication.isPlaying = false;
            }
            else
#endif
            {
                CloseLockFile();
                Application.Quit();
            }
        }

        private void CloseLockFile()
        {
            try
            { 
                if (_lockFileStream != null)
                {
                    _lockFileStream.Close();
                    
                    UnityEngine.Debug.Log("Deleting the lock file.");
                    File.Delete(_data.Value.LockFilePath);
                }
            }
            catch
            {
                UnityEngine.Debug.LogWarning("Lock file closing error");
            }
        }

        private void Awake()
        {
            UnityEngine.Assertions.Assert.raiseExceptions = true;

            _instance = this;
            UnityDispatcher.Initialize();
            Application.runInBackground = true;

            UnityEngine.Debug.LogFormat("patchkit-patcher-unity: {0}", Version.Value);
            UnityEngine.Debug.LogFormat("System version: {0}", EnvironmentInfo.GetSystemVersion());
            UnityEngine.Debug.LogFormat("Runtime version: {0}", EnvironmentInfo.GetSystemVersion());

            UnityEngine.Debug.Log("ASDAS");
            
            CheckEditorAppSecretSecure();

            if (_canStartThread)
            {
                StartThread();
            }
        }

        /// <summary>
        /// During patcher testing somebody may replace the secret with real game secret. If that would happen,
        /// patcher should quit immediatelly with following error.
        /// </summary>
        private void CheckEditorAppSecretSecure()
        {
            if (Application.isEditor)
            {
                return;
            }
            
            if (string.IsNullOrEmpty(EditorAppSecret) || EditorAppSecret.Trim() == EditorAllowedSecret)
            {
                return;
            }
            
            UnityEngine.Debug.LogError("Security issue: EditorAppSecret is set to not allowed value. " +
                                  "Please change it inside Unity editor to " + EditorAllowedSecret +
                                  " and build the project again.");
            Quit();
        }

        private void Update()
        {
            if (_thread == null || !_thread.IsAlive)
            {
                UnityEngine.Debug.Log("Quitting application because patcher thread is not alive.");
                Quit();
            }
        }

        private void OnApplicationQuit()
        {
            if (_thread != null && _thread.IsAlive)
            {
                UnityEngine.Debug.Log("Cancelling application quit because patcher thread is alive.");

                Application.CancelQuit();
                
                StartCoroutine(KillThread());
            }
        }

        private void OnDestroy()
        {
            _thread.Abort();
        }

        private void EnsureSingleInstance()
        {
            string lockFilePath = Data.Value.LockFilePath;
            UnityEngine.Debug.LogFormat("Opening lock file: {0}", lockFilePath);

            if (!string.IsNullOrEmpty(lockFilePath))
            {
                try
                {
                    _lockFileStream = File.Open(lockFilePath, FileMode.Append);
                    UnityEngine.Debug.Log("Lock file open success");
                }
                catch
                {
                    throw new MultipleInstancesException("Another instance of Patcher spotted");
                }
            }
            else
            {
                UnityEngine.Debug.LogWarning("LockFile is missing");
            }
        }
    }
}