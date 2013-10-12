using System;
using System.IO;
using System.Net;
using System.Threading;
using ServiceStack.Logging;
using ServiceStack.Text;
using ServiceStack.Web;

#if NETFX_CORE
using Windows.System.Threading;
#endif

namespace ServiceStack
{
    /**
     * Need to provide async request options
     * http://msdn.microsoft.com/en-us/library/86wf6409(VS.71).aspx
     */

    public partial class AsyncServiceClient
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(AsyncServiceClient));
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
        //private HttpWebRequest webRequest = null;

        /// <summary>
        /// The request filter is called before any request.
        /// This request filter is executed globally.
        /// </summary>
        public static Action<HttpWebRequest> RequestFilter { get; set; }

        /// <summary>
        /// The response action is called once the server response is available.
        /// It will allow you to access raw response information. 
        /// This response action is executed globally.
        /// Note that you should NOT consume the response stream as this is handled by ServiceStack
        /// </summary>
        public static Action<HttpWebResponse> ResponseFilter { get; set; }

        /// <summary>
        /// Called before request resend, when the initial request required authentication
        /// </summary>
        public Action<WebRequest> OnAuthenticationRequired { get; set; }

        public static int BufferSize = 8192;

        public ICredentials Credentials { get; set; }

        public bool AlwaysSendBasicAuthHeader { get; set; }

        public bool StoreCookies { get; set; }

        public CookieContainer CookieContainer { get; set; }

        /// <summary>
        /// The request filter is called before any request.
        /// This request filter only works with the instance where it was set (not global).
        /// </summary>
        public Action<HttpWebRequest> LocalHttpWebRequestFilter { get; set; }

        /// <summary>
        /// The response action is called once the server response is available.
        /// It will allow you to access raw response information. 
        /// Note that you should NOT consume the response stream as this is handled by ServiceStack
        /// </summary>
        public Action<HttpWebResponse> LocalHttpWebResponseFilter { get; set; }

        public string BaseUri { get; set; }
        public bool DisableAutoCompression { get; set; }

        public string UserName { get; set; }

        public string Password { get; set; }

        public void SetCredentials(string userName, string password)
        {
            this.UserName = userName;
            this.Password = password;
        }

        public TimeSpan? Timeout { get; set; }

        public string ContentType { get; set; }

        public StreamSerializerDelegate StreamSerializer { get; set; }

        public StreamDeserializerDelegate StreamDeserializer { get; set; }

        public bool HandleCallbackOnUIThread { get; set; }

        public bool EmulateHttpViaPost { get; set; }

#if SILVERLIGHT
        public bool ShareCookiesWithBrowser { get; set; }
#endif

        internal Action CancelAsyncFn;

        public void SendAsync<TResponse>(string httpMethod, string absoluteUrl, object request,
            Action<TResponse> onSuccess, Action<TResponse, Exception> onError)
        {
            SendWebRequest(httpMethod, absoluteUrl, request, onSuccess, onError);
        }

        public void CancelAsync()
        {
            if (CancelAsyncFn != null)
            {
                // Request will be nulled after it throws an exception on its async methods
                // See - http://msdn.microsoft.com/en-us/library/system.net.httpwebrequest.abort
                CancelAsyncFn();
                CancelAsyncFn = null;
            }
        }

        private AsyncState<TResponse> SendWebRequest<TResponse>(string httpMethod, string absoluteUrl, object request,
            Action<TResponse> onSuccess, Action<TResponse, Exception> onError)
        {
            if (httpMethod == null) throw new ArgumentNullException("httpMethod");

            var requestUri = absoluteUrl;
            var httpGetOrDeleteOrHead = (httpMethod == "GET" || httpMethod == "DELETE" || httpMethod == "HEAD");
            var hasQueryString = request != null && httpGetOrDeleteOrHead;
            if (hasQueryString)
            {
                var queryString = QueryStringSerializer.SerializeToString(request);
                if (!string.IsNullOrEmpty(queryString))
                {
                    requestUri += "?" + queryString;
                }
            }

            var webRequest = this.CreateHttpWebRequest(requestUri);

            var requestState = new AsyncState<TResponse>(BufferSize)
            {
                HttpMethod = httpMethod,
                Url = requestUri,
                WebRequest = webRequest,
                Request = request,
                OnSuccess = onSuccess,
                OnError = onError,
                HandleCallbackOnUIThread = HandleCallbackOnUIThread,
            };
            requestState.StartTimer(this.Timeout.GetValueOrDefault(DefaultTimeout));

            SendWebRequestAsync(httpMethod, request, requestState, webRequest);

            return requestState;
        }

        private void SendWebRequestAsync<TResponse>(string httpMethod, object request,
            AsyncState<TResponse> state, HttpWebRequest webRequest)
        {
            var httpGetOrDeleteOrHead = (httpMethod == "GET" || httpMethod == "DELETE" || httpMethod == "HEAD");
            webRequest.Accept = string.Format("{0}, */*", ContentType);

            //Methods others than GET and POST are only supported by Client request creator, see
            //http://msdn.microsoft.com/en-us/library/cc838250(v=vs.95).aspx
            
            if (this.EmulateHttpViaPost && httpMethod != "GET" && httpMethod != "POST") 
            {
                webRequest.Method = "POST"; 
                webRequest.Headers[HttpHeaders.XHttpMethodOverride] = httpMethod;
            }
            else
            {
                webRequest.Method = httpMethod;
            }

            if (this.Credentials != null) webRequest.Credentials = this.Credentials;
            if (this.AlwaysSendBasicAuthHeader) webRequest.AddBasicAuth(this.UserName, this.Password);

            ApplyWebRequestFilters(webRequest);

            try
            {
                if (!httpGetOrDeleteOrHead && request != null)
                {
                    webRequest.ContentType = ContentType;
                    webRequest.BeginGetRequestStream(RequestCallback<TResponse>, state);
                }
                else
                {
                    state.WebRequest.BeginGetResponse(ResponseCallback<TResponse>, state);
                }
            }
            catch (Exception ex)
            {
                // BeginGetRequestStream can throw if request was aborted
                HandleResponseError(ex, state);
            }
        }

        private void RequestCallback<T>(IAsyncResult asyncResult)
        {
            var requestState = (AsyncState<T>)asyncResult.AsyncState;
            try
            {
                var req = requestState.WebRequest;

                var stream = req.EndGetRequestStream(asyncResult);
                StreamSerializer(null, requestState.Request, stream);

                stream.EndAsyncStream();

                requestState.WebRequest.BeginGetResponse(ResponseCallback<T>, requestState);
            }
            catch (Exception ex)
            {
                HandleResponseError(ex, requestState);
            }
        }

#if NETFX_CORE
        private async void ResponseCallback<T>(IAsyncResult asyncResult)
#else
        private void ResponseCallback<T>(IAsyncResult asyncResult)
#endif
        {
            var requestState = (AsyncState<T>)asyncResult.AsyncState;
            try
            {
                var webRequest = requestState.WebRequest;

                requestState.WebResponse = (HttpWebResponse)webRequest.EndGetResponse(asyncResult);

                ApplyWebResponseFilters(requestState.WebResponse);

                if (typeof(T) == typeof(HttpWebResponse))
                {
                    requestState.HandleSuccess((T)(object)requestState.WebResponse);
                    return;
                }

                // Read the response into a Stream object.
                var responseStream = requestState.WebResponse.GetResponseStream();
                requestState.ResponseStream = responseStream;

#if NETFX_CORE
                var task = responseStream.ReadAsync(requestState.BufferRead, 0, BufferSize);
                ReadCallBack<T>(task, requestState);
#else
                responseStream.BeginRead(requestState.BufferRead, 0, BufferSize, ReadCallBack<T>, requestState);
#endif
                return;
            }
            catch (Exception ex)
            {
                var firstCall = Interlocked.Increment(ref requestState.RequestCount) == 1;
                if (firstCall && WebRequestUtils.ShouldAuthenticate(ex, this.UserName, this.Password))
                {
                    try
                    {
                        requestState.WebRequest = (HttpWebRequest)WebRequest.Create(requestState.Url);

                        if (StoreCookies)
                        {
                            requestState.WebRequest.CookieContainer = CookieContainer;
                        }

                        requestState.WebRequest.AddBasicAuth(this.UserName, this.Password);

                        if (OnAuthenticationRequired != null)
                        {
                            OnAuthenticationRequired(requestState.WebRequest);
                        }

                        SendWebRequestAsync(
                            requestState.HttpMethod, requestState.Request,
                            requestState, requestState.WebRequest);
                    }
                    catch (Exception /*subEx*/)
                    {
                        HandleResponseError(ex, requestState);
                    }
                    return;
                }

                HandleResponseError(ex, requestState);
            }
        }

#if NETFX_CORE
        private async void ReadCallBack<T>(Task<int> task, RequestState<T> requestState)
        {
#else
        private void ReadCallBack<T>(IAsyncResult asyncResult)
        {
            var requestState = (AsyncState<T>)asyncResult.AsyncState;
#endif
            try
            {
                var responseStream = requestState.ResponseStream;
#if NETFX_CORE
                int read = await task;
#else
                int read = responseStream.EndRead(asyncResult);
#endif

                if (read > 0)
                {
                    requestState.BytesData.Write(requestState.BufferRead, 0, read);
#if NETFX_CORE
                    var responeStreamTask = responseStream.ReadAsync(
                        requestState.BufferRead, 0, BufferSize);
                    ReadCallBack<T>(responeStreamTask, requestState);
#else
                    responseStream.BeginRead(
                        requestState.BufferRead, 0, BufferSize, ReadCallBack<T>, requestState);
#endif

                    return;
                }

                Interlocked.Increment(ref requestState.Completed);

                var response = default(T);
                try
                {
                    requestState.BytesData.Position = 0;
                    if (typeof(T) == typeof(Stream))
                    {
                        response = (T)(object)requestState.BytesData;
                    }
                    else
                    {
                        using (var reader = requestState.BytesData)
                        {
                            if (typeof(T) == typeof(string))
                            {
                                using (var sr = new StreamReader(reader))
                                {
                                    response = (T)(object)sr.ReadToEnd();
                                }
                            }
                            else if (typeof(T) == typeof(byte[]))
                            {
                                response = (T)(object)reader.ToArray();
                            }
                            else
                            {
                                response = (T)this.StreamDeserializer(typeof(T), reader);
                            }
                        }
                    }

#if SILVERLIGHT && !WINDOWS_PHONE && !NETFX_CORE
                    if (this.StoreCookies && this.ShareCookiesWithBrowser && !this.EmulateHttpViaPost)
                    {
                        // browser cookies must be set on the ui thread
                        System.Windows.Deployment.Current.Dispatcher.BeginInvoke(
                            () =>
                                {
                                    var cookieHeader = this.CookieContainer.GetCookieHeader(new Uri(BaseUri));
                                    System.Windows.Browser.HtmlPage.Document.Cookies = cookieHeader;
                                });
                    }
#endif

                    requestState.HandleSuccess(response);
                }
                catch (Exception ex)
                {
                    Log.Debug(string.Format("Error Reading Response Error: {0}", ex.Message), ex);
                    requestState.HandleError(default(T), ex);
                }
                finally
                {
#if NETFX_CORE
                    responseStream.Dispose();
#else
                    responseStream.Close();
#endif
                    CancelAsyncFn = null;
                }
            }
            catch (Exception ex)
            {
                HandleResponseError(ex, requestState);
            }
        }

        private void HandleResponseError<TResponse>(Exception exception, AsyncState<TResponse> state)
        {
            var webEx = exception as WebException;
            if (webEx != null
#if !SILVERLIGHT
 && webEx.Status == WebExceptionStatus.ProtocolError
#endif
)
            {
                var errorResponse = ((HttpWebResponse)webEx.Response);
                Log.Error(webEx);
                Log.DebugFormat("Status Code : {0}", errorResponse.StatusCode);
                Log.DebugFormat("Status Description : {0}", errorResponse.StatusDescription);

                var serviceEx = new WebServiceException(errorResponse.StatusDescription)
                {
                    StatusCode = (int)errorResponse.StatusCode,
                };

                try
                {
                    using (var stream = errorResponse.GetResponseStream())
                    {
                        //Uncomment to Debug exceptions:
                        //var strResponse = new StreamReader(stream).ReadToEnd();
                        //Console.WriteLine("Response: " + strResponse);
                        //stream.Position = 0;
                        serviceEx.ResponseBody = errorResponse.GetResponseStream().ReadFully().FromUtf8Bytes();
#if !MONOTOUCH
                        // MonoTouch throws NotSupportedException when setting System.Net.WebConnectionStream.Position
                        // Not sure if the stream is used later though, so may have to copy to MemoryStream and
                        // pass that around instead after this point?
                        stream.Position = 0;
#endif

                        serviceEx.ResponseDto = this.StreamDeserializer(typeof(TResponse), stream);
                        state.HandleError((TResponse)serviceEx.ResponseDto, serviceEx);
                    }
                }
                catch (Exception innerEx)
                {
                    // Oh, well, we tried
                    Log.Debug(string.Format("WebException Reading Response Error: {0}", innerEx.Message), innerEx);
                    state.HandleError(default(TResponse), new WebServiceException(errorResponse.StatusDescription, innerEx)
                    {
                        StatusCode = (int)errorResponse.StatusCode,
                    });
                }
                return;
            }

            var authEx = exception as AuthenticationException;
            if (authEx != null)
            {
                var customEx = WebRequestUtils.CreateCustomException(state.Url, authEx);

                Log.Debug(string.Format("AuthenticationException: {0}", customEx.Message), customEx);
                state.HandleError(default(TResponse), authEx);
            }

            Log.Debug(string.Format("Exception Reading Response Error: {0}", exception.Message), exception);
            state.HandleError(default(TResponse), exception);

            CancelAsyncFn = null;
        }

        private void ApplyWebResponseFilters(WebResponse webResponse)
        {
            if (!(webResponse is HttpWebResponse)) return;

            if (ResponseFilter != null)
                ResponseFilter((HttpWebResponse)webResponse);
            if (LocalHttpWebResponseFilter != null)
                LocalHttpWebResponseFilter((HttpWebResponse)webResponse);
        }

        private void ApplyWebRequestFilters(HttpWebRequest client)
        {
            if (LocalHttpWebRequestFilter != null)
                LocalHttpWebRequestFilter(client);

            if (RequestFilter != null)
                RequestFilter(client);
        }

        public void Dispose() { }
    }
}