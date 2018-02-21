﻿using System;
using JetBrains.Annotations;
using PatchKit.Api;
using PatchKit.Api.Models.Main;
using PatchKit.Logging;
using PatchKit.Network;
using PatchKit.Unity.Patcher.Debug;

namespace PatchKit.Unity.Patcher.AppData.Remote
{
    public class RemoteMetaData : IRemoteMetaData
    {
        private static readonly DebugLogger DebugLogger = new DebugLogger(typeof(RemoteMetaData));

        private readonly string _appSecret;
        private readonly MainApiConnection _mainApiConnectionWithoutRetry;
        private readonly MainApiConnection _mainApiConnection;
        private readonly KeysApiConnection _keysApiConnection;

        public RemoteMetaData([NotNull] string appSecret, [NotNull] IRequestTimeoutCalculator requestTimeoutCalculator)
        {
            if (string.IsNullOrEmpty(appSecret))
                throw new ArgumentException("Value cannot be null or empty.", "appSecret");
            if (requestTimeoutCalculator == null) throw new ArgumentNullException("requestTimeoutCalculator");

            DebugLogger.LogConstructor();
            DebugLogger.LogVariable(appSecret, "appSecret");

            _appSecret = appSecret;

            var mainSettings = Settings.GetMainApiConnectionSettings();

            _mainApiConnection = new MainApiConnection(mainSettings)
            {
                HttpClient = DependencyResolver.Resolve<IHttpClient>(),
                RequestTimeoutCalculator = requestTimeoutCalculator,
                RequestRetryStrategy = new SimpleInfiniteRequestRetryStrategy(),
                Logger = DependencyResolver.Resolve<ILogger>()
            };

            _mainApiConnectionWithoutRetry = new MainApiConnection(mainSettings)
            {
                HttpClient = DependencyResolver.Resolve<IHttpClient>(),
                RequestTimeoutCalculator = requestTimeoutCalculator,
                Logger = DependencyResolver.Resolve<ILogger>()
            };

            var keysSettings = Settings.GetKeysApiConnectionSettings();

            _keysApiConnection = new KeysApiConnection(keysSettings)
            {
                HttpClient = DependencyResolver.Resolve<IHttpClient>(),
                RequestTimeoutCalculator = requestTimeoutCalculator,
                Logger = DependencyResolver.Resolve<ILogger>()
            };
        }

        public int GetLatestVersionId(bool retryRequests = true)
        {
            DebugLogger.Log("Getting latest version id.");
            DebugLogger.Log("retryRequests = " + retryRequests);
            var m = retryRequests ? _mainApiConnection : _mainApiConnectionWithoutRetry;
            return m.GetAppLatestAppVersionId(_appSecret).Id;
        }

        public Api.Models.Main.App GetAppInfo(bool retryRequests = true)
        {
            DebugLogger.Log("Getting app info.");
            DebugLogger.Log("retryRequests = " + retryRequests);
            var m = retryRequests ? _mainApiConnection : _mainApiConnectionWithoutRetry;
            return m.GetApplicationInfo(_appSecret);
        }

        public AppContentSummary GetContentSummary(int versionId)
        {
            Checks.ArgumentValidVersionId(versionId, "versionId");
            DebugLogger.Log(string.Format("Getting content summary of version with id {0}.", versionId));

            return _mainApiConnection.GetAppVersionContentSummary(_appSecret, versionId);
        }

        public AppDiffSummary GetDiffSummary(int versionId)
        {
            Checks.ArgumentValidVersionId(versionId, "versionId");
            DebugLogger.Log(string.Format("Getting diff summary of version with id {0}.", versionId));

            return _mainApiConnection.GetAppVersionDiffSummary(_appSecret, versionId);
        }

        public string GetKeySecret(string key, string cachedKeySecret)
        {
            Checks.ArgumentNotNullOrEmpty(key, "key");
            DebugLogger.Log(string.Format("Getting key secret from key {0}.", key));

            var keySecret = _keysApiConnection.GetKeyInfo(key, _appSecret, cachedKeySecret).KeySecret;

            return keySecret;
        }
    }
}