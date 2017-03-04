﻿// Copyright © 2017 onwards, Andrew Whewell
// All rights reserved.
//
// Redistribution and use of this software in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//    * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//    * Neither the name of the author nor the names of the program's contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE AUTHORS OF THE SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Hosting;
using Owin;
using VirtualRadar.Interface;
using VirtualRadar.Interface.Settings;
using VirtualRadar.Interface.WebServer;

namespace VirtualRadar.WebServer.HttpListener
{
    /// <summary>
    /// An implementation of <see cref="IWebServer"/> that hooks into OWIN and uses
    /// Microsoft.Owin.Hosting to host the web site.
    /// </summary>
    class OwinWebServer : IWebServer
    {
        /// <summary>
        /// The shim that integrates the old style non-OWIN web site in with OWIN.
        /// </summary>
        private WebServerShim _OldServerShim;

        /// <summary>
        /// See interface docs.
        /// </summary>
        public AuthenticationSchemes AuthenticationScheme
        {
            get { return Provider.AuthenticationSchemes; }
            set { Provider.AuthenticationSchemes = value; }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public bool CacheCredentials { get; set; }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string ExternalAddress
        {
            get
            {
                string result = null;
                if(!String.IsNullOrEmpty(ExternalIPAddress)) {
                    result = String.Format("http://{0}{1}{2}",
                                ExternalIPAddress,
                                ExternalPort == 80 ? "" : String.Format(":{0}", ExternalPort),
                                Root);
                }
                return result;
            }
        }

        private string _ExternalIPAddress;
        /// <summary>
        /// See interface docs.
        /// </summary>
        public string ExternalIPAddress
        {
            get { return _ExternalIPAddress; }
            set
            {
                if(_ExternalIPAddress != value) {
                    _ExternalIPAddress = value;
                    OnExternalAddressChanged(EventArgs.Empty);
                }
            }
        }

        private int _ExternalPort;
        /// <summary>
        /// See interface docs.
        /// </summary>
        public int ExternalPort
        {
            get { return _ExternalPort; }
            set
            {
                if(_ExternalPort != value) {
                    _ExternalPort = value;
                    OnExternalAddressChanged(EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string LocalAddress
        {
            get { return String.Format("http://127.0.0.1{0}{1}", PortText, Root); }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string NetworkAddress
        {
            get
            {
                var ipAddress = NetworkIPAddress;
                return ipAddress == null ? null : String.Format("http://{0}{1}{2}", ipAddress, PortText, Root);
            }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string NetworkIPAddress
        {
            get
            {
                var ipAddresses = Provider.GetHostAddresses();
                var result = ipAddresses == null || ipAddresses.Length == 0 ? null : ipAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if(result != null && IPAddressHelper.IsLinkLocal(result)) {
                    var alternate = ipAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddressHelper.IsLinkLocal(a));
                    if(alternate != null) {
                        result = alternate;
                    }
                }
                return result == null ? null : result.ToString();
            }
        }

        private bool _Online;
        /// <summary>
        /// See interface docs.
        /// </summary>
        public bool Online
        {
            get { return _Online; }
            set
            {
                if(!value) {
                    if(_Online) {
                        StopHosting();
                    }
                } else {
                    if(!_Online) {
                        StartHosting();
                    }
                }
            }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string PortText
        {
            get { return Port == 80 ? "" : String.Format(":{0}", Port); }
        }

        /// <summary>
        /// See interface docs.
        /// </summary>
        public string Prefix
        {
            get { return String.Format("http://*:{0}{1}/", Port, Root == "/" ? "" : Root);; }
        }

        public IWebServerProvider Provider { get; set; } = new OwinWebServerProvider();

        private string _Root = "/";
        /// <summary>
        /// See interface docs.
        /// </summary>
        public string Root
        {
            get { return _Root; }
            set
            {
                string root = value ?? "/";
                if(root.Length == 0) root = "/";
                else {
                    if(root[0] != '/') root = String.Format("/{0}", root);
                    if(root.Length > 1 && root[root.Length - 1] == '/') root = root.Substring(0, root.Length - 1);
                }

                _Root = root;
            }
        }

        public event EventHandler<RequestReceivedEventArgs> AfterRequestReceived;

        internal void OnAfterRequestReceived(RequestReceivedEventArgs args)
        {
            EventHelper.RaiseQuickly(AfterRequestReceived, this, args);
        }

        public event EventHandler<AuthenticationRequiredEventArgs> AuthenticationRequired;

        internal void OnAuthenticationRequired(AuthenticationRequiredEventArgs args)
        {
            EventHelper.RaiseQuickly(AuthenticationRequired, this, args);
        }

        public event EventHandler<RequestReceivedEventArgs> BeforeRequestReceived;

        internal void OnBeforeRequestReceived(RequestReceivedEventArgs args)
        {
            EventHelper.RaiseQuickly(BeforeRequestReceived, this, args);
        }

        public event EventHandler<EventArgs<Exception>> ExceptionCaught;

        internal void OnExceptionCaught(EventArgs<Exception> args)
        {
            EventHelper.Raise(ExceptionCaught, this, args);
        }

        public event EventHandler ExternalAddressChanged;

        internal void OnExternalAddressChanged(EventArgs args)
        {
            EventHelper.Raise(ExternalAddressChanged, this, args);
        }

        public event EventHandler OnlineChanged;

        internal void OnOnlineChanged(EventArgs args)
        {
            EventHelper.Raise(OnlineChanged, this, args);
        }

        public event EventHandler<EventArgs<long>> RequestFinished;

        internal void OnRequestFinished(EventArgs<long> args)
        {
            EventHelper.RaiseQuickly(RequestFinished, this, args);
        }

        public event EventHandler<RequestReceivedEventArgs> RequestReceived;

        internal void OnRequestReceived(RequestReceivedEventArgs args)
        {
            EventHelper.RaiseQuickly(RequestReceived, this, args);
        }

        public event EventHandler<ResponseSentEventArgs> ResponseSent;

        internal void OnResponseSent(ResponseSentEventArgs args)
        {
            EventHelper.RaiseQuickly(ResponseSent, this, args);
        }

        public void AddAdministratorPath(string pathFromRoot)
        {
        }

        public void Dispose()
        {
            StopHosting();
        }

        public string[] GetAdministratorPaths()
        {
            return new string[] {};
        }

        public IDictionary<string, Access> GetRestrictedPathsMap()
        {
            return new Dictionary<string, Access>();
        }

        public void RemoveAdministratorPath(string pathFromRoot)
        {
        }

        public void ResetCredentialCache()
        {
        }

        public void SetRestrictedPath(string pathFromRoot, Access access)
        {
        }

        private IDisposable _WebApp;
        private void StartHosting()
        {
            if(_WebApp == null) {
                var startOptions = new StartOptions() {
                };
                startOptions.Urls.Add(Prefix);

                _WebApp = WebApp.Start(startOptions, ConfigureOwin);
                _Online = true;
                OnOnlineChanged(EventArgs.Empty);
            }
        }

        private void StopHosting()
        {
            if(_WebApp != null) {
                try {
                    _WebApp.Dispose();
                } finally {
                    _WebApp = null;
                    _Online = false;
                    OnOnlineChanged(EventArgs.Empty);
                }
            }
        }

        private void ConfigureOwin(IAppBuilder appBuilder)
        {
            _OldServerShim = new WebServerShim(this);
            _OldServerShim.Configure(appBuilder);
        }
    }
}