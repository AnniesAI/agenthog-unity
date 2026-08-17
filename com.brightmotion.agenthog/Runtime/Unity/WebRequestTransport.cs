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

        public void Send(string url, string json, string userAgent, Action<TransportStatus, int, string, string> callback)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                // teardown mid-send: report retryable so the batch survives via carry-over
                callback(TransportStatus.RetryableError, 0, null, null);
                return;
            }
            host.StartCoroutine(SendRoutine(url, json, userAgent, callback));
        }

        static IEnumerator SendRoutine(string url, string json, string userAgent, Action<TransportStatus, int, string, string> callback)
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
            string body = null;
            switch (req.result)
            {
                case UnityWebRequest.Result.Success: // any 2xx — 204, or 200 + attribution body
                    status = TransportStatus.Success;
                    body = req.downloadHandler.text;
                    break;
                case UnityWebRequest.Result.ProtocolError:
                    status = code >= 500 ? TransportStatus.RetryableError : TransportStatus.PermanentError;
                    break;
                default: // ConnectionError / DataProcessingError
                    status = TransportStatus.RetryableError;
                    break;
            }
            // flag-ruleset revision rides every ingest response (agent-hog CONTRACTS.md);
            // read it before Dispose or the header is gone
            string flagsRev = status == TransportStatus.Success ? req.GetResponseHeader("x-agh-flags-rev") : null;
            req.Dispose();
            callback(status, code, body, flagsRev);
        }

        public void Fetch(string url, string userAgent, Action<int, string> callback)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                callback(0, null);
                return;
            }
            host.StartCoroutine(FetchRoutine(url, userAgent, callback));
        }

        static IEnumerator FetchRoutine(string url, string userAgent, Action<int, string> callback)
        {
            var req = UnityWebRequest.Get(url);
            req.timeout = 30;
#if !UNITY_WEBGL || UNITY_EDITOR
            // WebGL: a bare GET needs no custom headers and stays preflight-free
            req.SetRequestHeader("User-Agent", userAgent);
#endif
            yield return req.SendWebRequest();
            int code = (int)req.responseCode;
            string body = req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null;
            req.Dispose();
            callback(body != null ? code : (code > 0 ? code : 0), body);
        }
    }
}
