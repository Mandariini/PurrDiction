using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PurrNet;
using UnityEngine;
using UnityEngine.Networking;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// Sends finished recordings to the PurrNet recordings service and deletes them once they are sent.
    /// Uploads run only while this game has no network session, one file at a time, so they never compete
    /// with match traffic; an upload cut off by a new session is retried later. The service answers each file
    /// with a short-lived upload URL (the bytes go straight to storage) or with "skip", in which case the file
    /// is deleted unsent. Recordings that cannot be sent are kept up to a size cap, oldest deleted first.
    /// The settings are remembered, so files left by an earlier session are sent from the next launch on,
    /// before any match scene loads.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class RecordingUploader : MonoBehaviour
    {
        const string Extension = ".purrrec";
        const float RetryDelaySeconds = 60f;
        const int TicketTimeoutSeconds = 20;
        const string PrefsKey = "PurrDiction.RecordingUploader";

        static RecordingUploader _instance;

        string _endpoint;
        string _projectKey;
        string _directory;
        long _maxLocalBytes;
        string _activePath;
        float _retryAt;
        bool _blocked;
        bool _uploading;
        UnityWebRequest _request;

        /// <summary>
        /// Starts uploading the recordings in <paramref name="directory"/> (and every file finished later).
        /// The first call creates the uploader; later calls update its settings.
        /// </summary>
        public static void Configure(string endpoint, string projectKey, string directory, long maxLocalBytes)
        {
            if (!_instance)
            {
                var go = new GameObject("PurrDiction RecordingUploader") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RecordingUploader>();
            }

            _instance._endpoint = endpoint.TrimEnd('/');
            _instance._projectKey = projectKey;
            _instance._directory = directory;
            _instance._maxLocalBytes = maxLocalBytes;

            var saved = JsonUtility.ToJson(new SavedSettings
            {
                endpoint = _instance._endpoint, projectKey = projectKey, directory = directory, maxLocalBytes = maxLocalBytes
            });
            if (PlayerPrefs.GetString(PrefsKey, "") != saved)
            {
                PlayerPrefs.SetString(PrefsKey, saved);
                PlayerPrefs.Save();
            }
        }

        [Serializable]
        struct SavedSettings
        {
            public string endpoint;
            public string projectKey;
            public string directory;
            public long maxLocalBytes;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void ResumeFromLastSession()
        {
            if (_instance)
                return;
            string json = PlayerPrefs.GetString(PrefsKey, "");
            if (string.IsNullOrEmpty(json))
                return;
            var saved = JsonUtility.FromJson<SavedSettings>(json);
            if (!string.IsNullOrEmpty(saved.endpoint) && !string.IsNullOrEmpty(saved.projectKey) && !string.IsNullOrEmpty(saved.directory))
                Configure(saved.endpoint, saved.projectKey, saved.directory, saved.maxLocalBytes);
        }

        /// <summary>The recording being written right now, which must not be sent or deleted.</summary>
        public static void SetActiveRecording(string path)
        {
            if (_instance)
                _instance._activePath = path;
        }

        /// <summary>A recording was finished: try sending it soon, and keep the folder under its cap.</summary>
        public static void NotifyFinished()
        {
            if (!_instance)
                return;
            _instance._retryAt = 0;
            _instance.TrimFolder();
        }

        void Update()
        {
            if (_uploading)
            {
                // A session started: get off the network, the file is sent again later.
                if (!IsIdle() && _request != null)
                    _request.Abort();
                return;
            }

            if (_blocked || Time.unscaledTime < _retryAt || !IsIdle() || string.IsNullOrEmpty(_projectKey))
                return;

            string next = OldestPending();
            if (next == null)
            {
                _retryAt = Time.unscaledTime + RetryDelaySeconds;
                return;
            }
            StartCoroutine(Send(next));
        }

        static bool IsIdle()
        {
            var manager = NetworkManager.main;
            return !manager || manager.isOffline;
        }

        string OldestPending()
        {
            if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
                return null;

            string oldest = null;
            DateTime oldestTime = DateTime.MaxValue;
            foreach (var path in Directory.GetFiles(_directory, "*" + Extension))
            {
                if (IsActive(path))
                    continue;
                var time = File.GetLastWriteTimeUtc(path);
                if (time < oldestTime)
                {
                    oldest = path;
                    oldestTime = time;
                }
            }
            return oldest;
        }

        bool IsActive(string path) =>
            !string.IsNullOrEmpty(_activePath) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(_activePath), StringComparison.OrdinalIgnoreCase);

        /// <summary>Deletes the oldest finished recordings until the folder fits its cap.</summary>
        void TrimFolder()
        {
            if (_maxLocalBytes <= 0 || string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
                return;

            var files = new List<FileInfo>();
            long total = 0;
            foreach (var path in Directory.GetFiles(_directory, "*" + Extension))
            {
                var info = new FileInfo(path);
                total += info.Length;
                if (!IsActive(path))
                    files.Add(info);
            }

            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Count && total > _maxLocalBytes; i++)
            {
                if (TryDelete(files[i].FullName))
                    total -= files[i].Length;
            }
        }

        [Serializable]
        struct TicketRequest
        {
            public string fileName;
            public long size;
        }

        [Serializable]
        struct TicketResponse
        {
            public bool skip;
            public string reason;
            public string id;
            public string url;
            public string contentType;
        }

        IEnumerator Send(string path)
        {
            _uploading = true;
            try
            {
                long size = new FileInfo(path).Length;
                var ticket = new TicketRequest { fileName = Path.GetFileName(path), size = size };

                TicketResponse response = default;
                bool ok = false;
                yield return PostJson(_endpoint + "/uploads", JsonUtility.ToJson(ticket), (code, text) =>
                {
                    if (code == 200 || code == 201)
                    {
                        response = JsonUtility.FromJson<TicketResponse>(text);
                        ok = true;
                    }
                    else
                        HandleRefusal(code, text);
                });
                if (!ok)
                    yield break;

                if (response.skip || string.IsNullOrEmpty(response.url))
                {
                    // Not wanted (sampling, a cap, a rejected file): sending it later would get the same answer.
                    TryDelete(path);
                    yield break;
                }

                using (var put = new UnityWebRequest(response.url, UnityWebRequest.kHttpVerbPUT))
                {
                    _request = put;
                    put.uploadHandler = new UploadHandlerFile(path);
                    put.downloadHandler = new DownloadHandlerBuffer();
                    put.SetRequestHeader("Content-Type", string.IsNullOrEmpty(response.contentType)
                        ? "application/octet-stream" : response.contentType);
                    yield return put.SendWebRequest();
                    _request = null;
                    if (put.result != UnityWebRequest.Result.Success)
                    {
                        _retryAt = Time.unscaledTime + RetryDelaySeconds;
                        yield break;
                    }
                }

                bool completed = false;
                yield return PostJson($"{_endpoint}/uploads/{UnityWebRequest.EscapeURL(response.id)}/complete", "{}", (code, text) =>
                {
                    if (code == 200)
                        completed = true;
                    else
                        HandleRefusal(code, text);
                });
                if (completed)
                    TryDelete(path);
            }
            finally
            {
                _request = null;
                _uploading = false;
            }
        }

        IEnumerator PostJson(string url, string json, Action<long, string> done)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            _request = request;
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = TicketTimeoutSeconds;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + _projectKey);
            yield return request.SendWebRequest();
            _request = null;
            done(request.responseCode, request.downloadHandler?.text);
        }

        void HandleRefusal(long code, string text)
        {
            // Wrong key or the service is not enabled for this project: stop until the next launch.
            // A quota is a "not now" (it is also a 403).
            bool quota = text != null && text.Contains("quota");
            if (code == 401 || (code == 403 && !quota))
            {
                if (!_blocked)
                    Debug.LogWarning($"[RecordingUploader] Uploads refused ({code}): {text}");
                _blocked = true;
                return;
            }

            // Rate limits, busy server, network errors: try again later.
            _retryAt = Time.unscaledTime + RetryDelaySeconds;
        }

        static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void OnDestroy()
        {
            if (_request != null)
                _request.Abort();
            if (_instance == this)
                _instance = null;
        }
    }
}
