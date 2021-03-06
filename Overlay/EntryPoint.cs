﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Runtime.Serialization.Formatters;
using System.Threading;
using System.Threading.Tasks;
using EasyHook;
using Overlay.Hook;
using Overlay.Interface;

namespace Overlay
{
    public class EntryPoint : IEntryPoint
    {
        readonly List<IDXHook> _directXHooks = new List<IDXHook>();
        private IDXHook _directXHook;
        private readonly OverlayInterface _interface;
        private ManualResetEvent _runWait;
        private readonly IpcServerChannel _clientServerChannel = null;

        public EntryPoint(
            RemoteHooking.IContext context,
            string channelName,
            OverlayConfig config)
        {
            // Get reference to IPC to host application
            // Note: any methods called or events triggered against _interface will execute in the host process.
            _interface = RemoteHooking.IpcConnectClient<OverlayInterface>(channelName);

            // We try to ping immediately, if it fails then injection fails
            _interface.Ping();

            #region Allow client event handlers (bi-directional IPC)
            
            // Attempt to create a IpcServerChannel so that any event handlers on the client will function correctly
            IDictionary properties = new Hashtable();
            properties["name"] = channelName;
            properties["portName"] = channelName + Guid.NewGuid().ToString("N"); // random portName so no conflict with existing channels of channelName

            var binaryProv = new BinaryServerFormatterSinkProvider();
            binaryProv.TypeFilterLevel = TypeFilterLevel.Full;

            var clientServerChannel = new IpcServerChannel(properties, binaryProv);
            ChannelServices.RegisterChannel(clientServerChannel, false);
            
            #endregion
        }

        public void Run(
            RemoteHooking.IContext context,
            string channelName,
            OverlayConfig config)
        {
            // When not using GAC there can be issues with remoting assemblies resolving correctly
            // this is a workaround that ensures that the current assembly is correctly associated
            var currentDomain = AppDomain.CurrentDomain;
            currentDomain.AssemblyResolve += (sender, args) => GetType().Assembly.FullName == args.Name ? GetType().Assembly : null;

            // NOTE: This is running in the target process
            _interface.Message(MessageType.Information, "Injected into process Id:{0}.", RemoteHooking.GetCurrentProcessId());

            _runWait = new ManualResetEvent(false);
            _runWait.Reset();
            try
            {
                // Initialise the Hook
                if (!InitialiseDirectXHook(config))
                {
                    return;
                }


                // We start a thread here to periodically check if the host is still running
                // If the host process stops then we will automatically uninstall the hooks
                StartCheckHostIsAliveThread();

                // Wait until signaled for exit either when a Disconnect message from the host 
                // or if the the check is alive has failed to Ping the host.
                _runWait.WaitOne();

                // we need to tell the check host thread to exit (if it hasn't already)
                StopCheckHostIsAliveThread();

                // Dispose of the DXHook so any installed hooks are removed correctly
                DisposeDirectXHook();
            }
            catch (Exception e)
            {
                _interface.Message(MessageType.Error, "An unexpected error occured: {0}", e.ToString());
            }
            finally
            {
                try
                {
                    _interface.Message(MessageType.Information, "Disconnecting from process {0}", RemoteHooking.GetCurrentProcessId());
                }
                catch
                {
                }

                // Remove the client server channel (that allows client event handlers)
                ChannelServices.UnregisterChannel(_clientServerChannel);

                // Always sleep long enough for any remaining messages to complete sending
                Thread.Sleep(100);
            }
        }

        private void DisposeDirectXHook()
        {
            if (_directXHooks != null)
            {
                try
                {
                    _interface.Message(MessageType.Debug, "Disposing of hooks...");
                }
                catch (RemotingException) { } // Ignore channel remoting errors

                // Dispose of the hooks so they are removed
                foreach (var dxHook in _directXHooks)
                    dxHook.Dispose();

                _directXHooks.Clear();
            }
        }

        private bool InitialiseDirectXHook(OverlayConfig config)
        {
            var version = config.Direct3DVersion;

            var loadedVersions = new List<Direct3DVersion>();

            var isX64Process = RemoteHooking.IsX64Process(RemoteHooking.GetCurrentProcessId());
            _interface.Message(MessageType.Information, "Remote process is a {0}-bit process.", isX64Process ? "64" : "32");

            try
            {
                if (version == Direct3DVersion.Unknown)
                {
                    // Attempt to determine the correct version based on loaded module.
                    // In most cases this will work fine, however it is perfectly ok for an application to use a D3D10 device along with D3D11 devices
                    // so the version might matched might not be the one you want to use
                    var d3D9Loaded = IntPtr.Zero;
                    var d3D10Loaded = IntPtr.Zero;
                    var d3D101Loaded = IntPtr.Zero;
                    var d3D11Loaded = IntPtr.Zero;
                    var d3D111Loaded = IntPtr.Zero;

                    var delayTime = 100;
                    var retryCount = 0;
                    while (d3D9Loaded == IntPtr.Zero && d3D10Loaded == IntPtr.Zero && d3D101Loaded == IntPtr.Zero && d3D11Loaded == IntPtr.Zero && d3D111Loaded == IntPtr.Zero)
                    {
                        retryCount++;
                        d3D9Loaded = NativeMethods.GetModuleHandle("d3d9.dll");
                        d3D10Loaded = NativeMethods.GetModuleHandle("d3d10.dll");
                        d3D101Loaded = NativeMethods.GetModuleHandle("d3d10_1.dll");
                        d3D11Loaded = NativeMethods.GetModuleHandle("d3d11.dll");
                        d3D111Loaded = NativeMethods.GetModuleHandle("d3d11_1.dll");
                        Thread.Sleep(delayTime);

                        if (retryCount * delayTime > 5000)
                        {
                            _interface.Message(MessageType.Error, "Unsupported Direct3D version, or Direct3D DLL not loaded within 5 seconds.");
                            return false;
                        }
                    }

                    version = Direct3DVersion.Direct3D9;
                    if (d3D9Loaded != IntPtr.Zero)
                    {
                        _interface.Message(MessageType.Debug, "Autodetect found Direct3D 9");
                        version = Direct3DVersion.Direct3D9;
                        loadedVersions.Add(version);
                    }
                }
                else
                {
                    // If not autodetect, assume specified version is loaded
                    loadedVersions.Add(version);
                }

                foreach (var dxVersion in loadedVersions)
                {
                    version = dxVersion;
                    switch (version)
                    {
                        case Direct3DVersion.Direct3D9:
                            _directXHook = new DxHookD3D9(_interface);
                            break;
                        default:
                            _interface.Message(MessageType.Error, "Unsupported Direct3D version: {0}", version);
                            return false;
                    }

                    _directXHook.Config = config;
                    _directXHook.Hook();

                    _directXHooks.Add(_directXHook);
                }

                return true;

            }
            catch (Exception e)
            {
                // Notify the host/server application about this error
                _interface.Message(MessageType.Error, "Error in InitialiseHook: {0}", e.ToString());
                return false;
            }
        }

        #region Check Host Is Alive

        Task _checkAlive;
        long _stopCheckAlive;
        
        /// <summary>
        /// Begin a background thread to check periodically that the host process is still accessible on its IPC channel
        /// </summary>
        private void StartCheckHostIsAliveThread()
        {
            _checkAlive = new Task(() =>
            {
                try
                {
                    while (Interlocked.Read(ref _stopCheckAlive) == 0)
                    {
                        Thread.Sleep(1000);

                        // .NET Remoting exceptions will throw RemotingException
                        _interface.Ping();
                    }
                }
                catch // We will assume that any exception means that the hooks need to be removed. 
                {
                    // Signal the Run method so that it can exit
                    _runWait.Set();
                }
            });

            _checkAlive.Start();
        }

        /// <summary>
        /// Tell the _checkAlive thread that it can exit if it hasn't already
        /// </summary>
        private void StopCheckHostIsAliveThread()
        {
            Interlocked.Increment(ref _stopCheckAlive);
        }

        #endregion
    }
}
