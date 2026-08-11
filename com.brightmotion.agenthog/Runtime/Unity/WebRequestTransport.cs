using System;
using System.Collections;
using System.Text;
using Brightmotion.AgentHog.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>ITransport over UnityWebRequest, coroutine-hosted on the AgentHog runner.</summary>
    internal sealed class WebRequestTransport : ITransport
    {
        readonly MonoBehaviour host;

        public WebRequestTransport(MonoBehaviour host) => this.host = host;

        public void Send(string url, string json, string userAgent, Action<TransportStatus, int> callback)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                // teardown mid-send: report retryable so the batch survives via carry-over
                callback(TransportStatus.RetryableError, 0);
                return;
            }
            host.StartCoroutine(SendRoutine(url, json, userAgent, callback));
        }

        static IEnumerator SendRoutine(string url, string json, string userAgent, Action<TransportStatus, int> callback)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30,
            };
#if UNITY_WEBGL && !UNITY_EDITOR
            // browsers forbid a custom User-Agent and text/plain dodges the CORS preflight —
            // the server parses the body as JSON regardless of content-type
            req.SetRequestHeader("Content-Type", "text/plain");
#else
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("User-Agent", userAgent);
#endif
            yield return req.SendWebRequest();

            int code = (int)req.responseCode;
            TransportStatus status;
            switch (req.result)
            {
                case UnityWebRequest.Result.Success:
                    status = TransportStatus.Success;
                    break;
                case UnityWebRequest.Result.ProtocolError:
                    status = code >= 500 ? TransportStatus.RetryableError : TransportStatus.PermanentError;
                    break;
                default: // ConnectionError / DataProcessingError
                    status = TransportStatus.RetryableError;
                    break;
            }
            req.Dispose();
            callback(status, code);
        }
    }
}
