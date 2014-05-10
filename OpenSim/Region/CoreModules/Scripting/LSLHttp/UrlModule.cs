/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Threading;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Scripting.LSLHttp
{
    /// <summary>
    /// Data describing an external URL set up by a script.
    /// </summary>
    public class UrlData
    {
        /// <summary>
        /// Scene object part hosting the script
        /// </summary>
        public UUID hostID;

        /// <summary>
        /// The item ID of the script that requested the URL.
        /// </summary>
        public UUID itemID;

        /// <summary>
        /// The script engine that runs the script.
        /// </summary>
        public IScriptModule engine;

        /// <summary>
        /// The generated URL.
        /// </summary>
        public string url;

        /// <summary>
        /// The random UUID component of the generated URL.
        /// </summary>
        public UUID urlcode;

        /// <summary>
        /// The external requests currently being processed or awaiting retrieval for this URL.
        /// </summary>
        public Dictionary<UUID, RequestData> requests;
    }

    public class RequestData
    {
        public UUID requestID;
        public Dictionary<string, string> headers;
        public string body;
        public int responseCode;
        public string responseBody;
        public string responseType = "text/plain";
        //public ManualResetEvent ev;
        public bool requestDone;
        public int startTime;
        public string uri;
    }

    /// <summary>
    /// This module provides external URLs for in-world scripts.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "UrlModule")]
    public class UrlModule : ISharedRegionModule, IUrlModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Indexs the URL request metadata (which script requested it, outstanding requests, etc.) by the request ID
        /// randomly generated when a request is received for this URL.
        /// </summary>
        /// <remarks>
        /// Manipulation or retrieval from this dictionary must be locked on m_UrlMap to preserve consistency with
        /// m_UrlMap
        /// </remarks>
        private Dictionary<UUID, UrlData> m_RequestMap = new Dictionary<UUID, UrlData>();

        /// <summary>
        /// Indexs the URL request metadata (which script requested it, outstanding requests, etc.) by the full URL
        /// </summary>
        private Dictionary<string, UrlData> m_UrlMap = new Dictionary<string, UrlData>();
        private ReaderWriterLock m_UrlMapRwLock = new ReaderWriterLock();

        private uint m_HttpsPort = 0;
        private IHttpServer m_HttpServer = null;
        private IHttpServer m_HttpsServer = null;

        public string ExternalHostNameForLSL { get; private set; }

        /// <summary>
        /// The default maximum number of urls
        /// </summary>
        public const int DefaultTotalUrls = 100;

        /// <summary>
        /// Maximum number of external urls that can be set up by this module.
        /// </summary>
        public int TotalUrls { get; set; }

        public Type ReplaceableInterface 
        {
            get { return null; }
        }

        public string Name
        {
            get { return "UrlModule"; }
        }

        public void Initialise(IConfigSource config)
        {
            IConfig networkConfig = config.Configs["Network"];

            if (networkConfig != null)
            {
                ExternalHostNameForLSL = config.Configs["Network"].GetString("ExternalHostNameForLSL", null);

                bool ssl_enabled = config.Configs["Network"].GetBoolean("https_listener", false);

                if (ssl_enabled)
                    m_HttpsPort = (uint)config.Configs["Network"].GetInt("https_port", (int)m_HttpsPort);
            }

            if (ExternalHostNameForLSL == null)
                ExternalHostNameForLSL = System.Environment.MachineName;

            IConfig llFunctionsConfig = config.Configs["LL-Functions"];

            if (llFunctionsConfig != null)
                TotalUrls = llFunctionsConfig.GetInt("max_external_urls_per_simulator", DefaultTotalUrls);
            else
                TotalUrls = DefaultTotalUrls;
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (m_HttpServer == null)
            {
                // There can only be one
                //
                m_HttpServer = MainServer.Instance;
                //
                // We can use the https if it is enabled
                if (m_HttpsPort > 0)
                {
                    m_HttpsServer = MainServer.GetHttpServer(m_HttpsPort);
                }
            }

            scene.RegisterModuleInterface<IUrlModule>(this);

            scene.EventManager.OnScriptReset += OnScriptReset;
        }

        public void RegionLoaded(Scene scene)
        {
            IScriptModule[] scriptModules = scene.RequestModuleInterfaces<IScriptModule>();
            foreach (IScriptModule scriptModule in scriptModules)
            {
                scriptModule.OnScriptRemoved += ScriptRemoved;
                scriptModule.OnObjectRemoved += ObjectRemoved;
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public UUID RequestURL(IScriptModule engine, SceneObjectPart host, UUID itemID)
        {
            UUID urlcode = UUID.Random();

            m_UrlMapRwLock.AcquireWriterLock(-1);
            try
            {
                if (m_UrlMap.Count >= TotalUrls)
                {
                    engine.PostScriptEvent(itemID, "http_request", new Object[] { urlcode.ToString(), "URL_REQUEST_DENIED", "" });
                    return urlcode;
                }
                string url = "http://" + ExternalHostNameForLSL + ":" + m_HttpServer.Port.ToString() + "/lslhttp/" + urlcode.ToString() + "/";

                UrlData urlData = new UrlData();
                urlData.hostID = host.UUID;
                urlData.itemID = itemID;
                urlData.engine = engine;
                urlData.url = url;
                urlData.urlcode = urlcode;
                urlData.requests = new Dictionary<UUID, RequestData>();
                
                m_UrlMap[url] = urlData;
                
                string uri = "/lslhttp/" + urlcode.ToString() + "/";
               
                PollServiceEventArgs args 
                    = new PollServiceEventArgs(HttpRequestHandler, uri, HasEvents, GetEvents, NoEvents, urlcode, 25000);
                args.Type = PollServiceEventArgs.EventType.LslHttp;
                m_HttpServer.AddPollServiceHTTPHandler(uri, args);

                m_log.DebugFormat(
                    "[URL MODULE]: Set up incoming request url {0} for {1} in {2} {3}",
                    uri, itemID, host.Name, host.LocalId);

                engine.PostScriptEvent(itemID, "http_request", new Object[] { urlcode.ToString(), "URL_REQUEST_GRANTED", url });
            }
            finally
            {
                m_UrlMapRwLock.ReleaseWriterLock();
            }

            return urlcode;
        }

        public UUID RequestSecureURL(IScriptModule engine, SceneObjectPart host, UUID itemID)
        {
            UUID urlcode = UUID.Random();

            if (m_HttpsServer == null)
            {
                engine.PostScriptEvent(itemID, "http_request", new Object[] { urlcode.ToString(), "URL_REQUEST_DENIED", "" });
                return urlcode;
            }

            m_UrlMapRwLock.AcquireWriterLock(-1);
            try
            {
                if (m_UrlMap.Count >= TotalUrls)
                {
                    engine.PostScriptEvent(itemID, "http_request", new Object[] { urlcode.ToString(), "URL_REQUEST_DENIED", "" });
                    return urlcode;
                }
                string url = "https://" + ExternalHostNameForLSL + ":" + m_HttpsServer.Port.ToString() + "/lslhttps/" + urlcode.ToString() + "/";

                UrlData urlData = new UrlData();
                urlData.hostID = host.UUID;
                urlData.itemID = itemID;
                urlData.engine = engine;
                urlData.url = url;
                urlData.urlcode = urlcode;
                urlData.requests = new Dictionary<UUID, RequestData>();

                m_UrlMap[url] = urlData;
                
                string uri = "/lslhttps/" + urlcode.ToString() + "/";
               
                PollServiceEventArgs args 
                    = new PollServiceEventArgs(HttpRequestHandler, uri, HasEvents, GetEvents, NoEvents, urlcode, 25000);
                args.Type = PollServiceEventArgs.EventType.LslHttp;
                m_HttpsServer.AddPollServiceHTTPHandler(uri, args);

                m_log.DebugFormat(
                    "[URL MODULE]: Set up incoming secure request url {0} for {1} in {2} {3}",
                    uri, itemID, host.Name, host.LocalId);

                engine.PostScriptEvent(itemID, "http_request", new Object[] { urlcode.ToString(), "URL_REQUEST_GRANTED", url });
            }
            finally
            {
                m_UrlMapRwLock.ReleaseWriterLock();
            }

            return urlcode;
        }

        public void ReleaseURL(string url)
        {
            m_UrlMapRwLock.AcquireWriterLock(-1);
            try
            {
                UrlData data;

                if (!m_UrlMap.TryGetValue(url, out data))
                {
                    return;
                }

                foreach (UUID req in data.requests.Keys)
                    m_RequestMap.Remove(req);

                m_log.DebugFormat(
                    "[URL MODULE]: Releasing url {0} for {1} in {2}",
                    url, data.itemID, data.hostID);

                RemoveUrl(data);
                m_UrlMap.Remove(url);
            }
            finally
            {
                m_UrlMapRwLock.ReleaseWriterLock();
            }
        }
        
        public void HttpContentType(UUID request, string type)
        {
            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                if (m_RequestMap.ContainsKey(request))
                {
                    UrlData urlData = m_RequestMap[request];
                    urlData.requests[request].responseType = type;
                }
                else
                {
                    m_log.Info("[HttpRequestHandler] There is no http-in request with id " + request.ToString());
                }
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }
        }
        
        public void HttpResponse(UUID request, int status, string body)
        {
            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                if (m_RequestMap.ContainsKey(request))
                {
                    UrlData urlData = m_RequestMap[request];
                    string responseBody = body;
                    if (urlData.requests[request].responseType.Equals("text/plain"))
                    {
                        string value;
                        if (urlData.requests[request].headers.TryGetValue("user-agent", out value))
                        {
                            if (value != null && value.IndexOf("MSIE") >= 0)
                            {
                                // wrap the html escaped response if the target client is IE
                                // It ignores "text/plain" if the body is html
                                responseBody = "<html>" + System.Web.HttpUtility.HtmlEncode(body) + "</html>";
                            }
                        }
                    }
                    urlData.requests[request].responseCode = status;
                    urlData.requests[request].responseBody = responseBody;
                    //urlData.requests[request].ev.Set();
                    urlData.requests[request].requestDone =true;
                }
                else
                {
                    m_log.Info("[HttpRequestHandler] There is no http-in request with id " + request.ToString());
                }
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }
        }

        public string GetHttpHeader(UUID requestId, string header)
        {
            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                if (m_RequestMap.ContainsKey(requestId))
                {
                    UrlData urlData = m_RequestMap[requestId];
                    string value;
                    if (urlData.requests[requestId].headers.TryGetValue(header, out value))
                        return value;
                }
                else
                {
                    m_log.Warn("[HttpRequestHandler] There was no http-in request with id " + requestId);
                }
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }

            return String.Empty;
        }

        public int GetFreeUrls()
        {
            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                return TotalUrls - m_UrlMap.Count;
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }
        }

        public void ScriptRemoved(UUID itemID)
        {
//            m_log.DebugFormat("[URL MODULE]: Removing script {0}", itemID);

            m_UrlMapRwLock.AcquireWriterLock(-1);
            try
            {
                List<string> removeURLs = new List<string>();

                foreach (KeyValuePair<string, UrlData> url in m_UrlMap)
                {
                    if (url.Value.itemID == itemID)
                    {
                        RemoveUrl(url.Value);
                        removeURLs.Add(url.Key);
                        foreach (UUID req in url.Value.requests.Keys)
                            m_RequestMap.Remove(req);
                    }
                }

                foreach (string urlname in removeURLs)
                    m_UrlMap.Remove(urlname);
            }
            finally
            {
                m_UrlMapRwLock.ReleaseWriterLock();
            }
        }

        public void ObjectRemoved(UUID objectID)
        {
            m_UrlMapRwLock.AcquireWriterLock(-1);
            try
            {
                List<string> removeURLs = new List<string>();

                foreach (KeyValuePair<string, UrlData> url in m_UrlMap)
                {
                    if (url.Value.hostID == objectID)
                    {
                        RemoveUrl(url.Value);
                        removeURLs.Add(url.Key);

                        foreach (UUID req in url.Value.requests.Keys)
                            m_RequestMap.Remove(req);
                    }
                }

                foreach (string urlname in removeURLs)
                    m_UrlMap.Remove(urlname);
            }
            finally
            {
                m_UrlMapRwLock.ReleaseWriterLock();
            }
        }

        private void RemoveUrl(UrlData data)
        {
            m_HttpServer.RemoveHTTPHandler("", "/lslhttp/" + data.urlcode.ToString() + "/");
        }

        private Hashtable NoEvents(UUID requestID, UUID sessionID)
        {
            Hashtable response = new Hashtable();
            UrlData urlData;

            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                // We need to return a 404 here in case the request URL was removed at exactly the same time that a
                // request was made.  In this case, the request thread can outrace llRemoveURL() and still be polling
                // for the request ID.
                if (!m_RequestMap.ContainsKey(requestID))
                {
                    response["int_response_code"] = 404;
                    response["str_response_string"] = "";
                    response["keepalive"] = false;
                    response["reusecontext"] = false;

                    return response;
                }

                urlData = m_RequestMap[requestID];

                if (System.Environment.TickCount - urlData.requests[requestID].startTime > 25000)
                {
                    response["int_response_code"] = 500;
                    response["str_response_string"] = "Script timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;
                    response["reusecontext"] = false;

                    LockCookie lc = m_UrlMapRwLock.UpgradeToWriterLock(-1);
                    try
                    {
                        //remove from map
                        urlData.requests.Remove(requestID);
                        m_RequestMap.Remove(requestID);
                    }
                    finally
                    {
                        m_UrlMapRwLock.DowngradeFromWriterLock(ref lc);
                    }

                    return response;
                }
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }

            return response;
        }

        private bool HasEvents(UUID requestID, UUID sessionID)
        {
            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                // We return true here because an external URL request that happened at the same time as an llRemoveURL()
                // can still make it through to HttpRequestHandler().  That will return without setting up a request
                // when it detects that the URL has been removed.  The poller, however, will continue to ask for
                // events for that request, so here we will signal that there are events and in GetEvents we will
                // return a 404.
                if (!m_RequestMap.ContainsKey(requestID))
                {
                    return true;
                }

                UrlData urlData = m_RequestMap[requestID];

                if (!urlData.requests.ContainsKey(requestID))
                {
                    return true;
                }

                // Trigger return of timeout response.
                if (System.Environment.TickCount - urlData.requests[requestID].startTime > 25000)
                {
                    return true;
                }

                return urlData.requests[requestID].requestDone;
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }
        }

        private Hashtable GetEvents(UUID requestID, UUID sessionID)
        {
            Hashtable response;

            m_UrlMapRwLock.AcquireReaderLock(-1);
            try
            {
                UrlData url = null;
                RequestData requestData = null;

                if (!m_RequestMap.ContainsKey(requestID))
                    return NoEvents(requestID, sessionID);

                url = m_RequestMap[requestID];
                requestData = url.requests[requestID];

                if (!requestData.requestDone)
                    return NoEvents(requestID, sessionID);

                response = new Hashtable();

                if (System.Environment.TickCount - requestData.startTime > 25000)
                {
                    response["int_response_code"] = 500;
                    response["str_response_string"] = "Script timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;
                    response["reusecontext"] = false;
                    return response;
                }

                //put response
                response["int_response_code"] = requestData.responseCode;
                response["str_response_string"] = requestData.responseBody;
                response["content_type"] = requestData.responseType;
                // response["content_type"] = "text/plain";
                response["keepalive"] = false;
                response["reusecontext"] = false;

                LockCookie lc = m_UrlMapRwLock.UpgradeToWriterLock(-1);
                try
                {
                    //remove from map
                    url.requests.Remove(requestID);
                    m_RequestMap.Remove(requestID);
                }
                finally
                {
                    m_UrlMapRwLock.DowngradeFromWriterLock(ref lc);
                }
            }
            finally
            {
                m_UrlMapRwLock.ReleaseReaderLock();
            }

            return response;
        }

        public void HttpRequestHandler(UUID requestID, Hashtable request)
        {
            string uri = request["uri"].ToString();
            bool is_ssl = uri.Contains("lslhttps");

            try
            {
                Hashtable headers = (Hashtable)request["headers"];

//                    string uri_full = "http://" + m_ExternalHostNameForLSL + ":" + m_HttpServer.Port.ToString() + uri;// "/lslhttp/" + urlcode.ToString() + "/";

                int pos1 = uri.IndexOf("/");// /lslhttp
                int pos2 = uri.IndexOf("/", pos1 + 1);// /lslhttp/
                int pos3 = uri.IndexOf("/", pos2 + 1);// /lslhttp/<UUID>/
                string uri_tmp = uri.Substring(0, pos3 + 1);
                //HTTP server code doesn't provide us with QueryStrings
                string pathInfo;
                string queryString;
                queryString = "";

                pathInfo = uri.Substring(pos3);

                UrlData urlData = null;

                m_UrlMapRwLock.AcquireReaderLock(-1);
                try
                {
                    string url;

                    if (is_ssl)
                        url = "https://" + ExternalHostNameForLSL + ":" + m_HttpsServer.Port.ToString() + uri_tmp;
                    else
                        url = "http://" + ExternalHostNameForLSL + ":" + m_HttpServer.Port.ToString() + uri_tmp;

                    // Avoid a race - the request URL may have been released via llRequestUrl() whilst this
                    // request was being processed.
                    if (!m_UrlMap.TryGetValue(url, out urlData))
                        return;

                    //for llGetHttpHeader support we need to store original URI here
                    //to make x-path-info / x-query-string / x-script-url / x-remote-ip headers 
                    //as per http://wiki.secondlife.com/wiki/LlGetHTTPHeader

                    RequestData requestData = new RequestData();
                    requestData.requestID = requestID;
                    requestData.requestDone = false;
                    requestData.startTime = System.Environment.TickCount;
                    requestData.uri = uri;
                    if (requestData.headers == null)
                        requestData.headers = new Dictionary<string, string>();

                    foreach (DictionaryEntry header in headers)
                    {
                        string key = (string)header.Key;
                        string value = (string)header.Value;
                        requestData.headers.Add(key, value);
                    }

                    foreach (DictionaryEntry de in request)
                    {
                        if (de.Key.ToString() == "querystringkeys")
                        {
                            System.String[] keys = (System.String[])de.Value;
                            foreach (String key in keys)
                            {
                                if (request.ContainsKey(key))
                                {
                                    string val = (String)request[key];
                                    queryString = queryString + key + "=" + val + "&";
                                }
                            }

                            if (queryString.Length > 1)
                                queryString = queryString.Substring(0, queryString.Length - 1);
                        }
                    }

                    //if this machine is behind DNAT/port forwarding, currently this is being
                    //set to address of port forwarding router
                    requestData.headers["x-remote-ip"] = requestData.headers["remote_addr"];
                    requestData.headers["x-path-info"] = pathInfo;
                    requestData.headers["x-query-string"] = queryString;
                    requestData.headers["x-script-url"] = urlData.url;

                    LockCookie lc = m_UrlMapRwLock.UpgradeToWriterLock(-1);
                    try
                    {
                        urlData.requests.Add(requestID, requestData);
                        m_RequestMap.Add(requestID, urlData);
                    }
                    finally
                    {
                        m_UrlMapRwLock.DowngradeFromWriterLock(ref lc);
                    }
                }
                finally
                {
                    m_UrlMapRwLock.ReleaseReaderLock();
                }

                urlData.engine.PostScriptEvent(
                    urlData.itemID,
                    "http_request",
                    new Object[] { requestID.ToString(), request["http-method"].ToString(), request["body"].ToString() });
            }
            catch (Exception we)
            {
                //Hashtable response = new Hashtable();
                m_log.Warn("[HttpRequestHandler]: http-in request failed");
                m_log.Warn(we.Message);
                m_log.Warn(we.StackTrace);
            }
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            ScriptRemoved(itemID);
        }
    }
}
