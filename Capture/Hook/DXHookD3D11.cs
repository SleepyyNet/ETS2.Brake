﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX;
using System.Runtime.InteropServices;
using EasyHook;
using System.Threading;
using System.IO;
using Capture.Interface;
using SharpDX.Direct3D;

namespace Capture.Hook
{
    enum D3D11DeviceVTbl : short
    {
        // IUnknown
        QueryInterface = 0,
        AddRef = 1,
        Release = 2,

        // ID3D11Device
        CreateBuffer = 3,
        CreateTexture1D = 4,
        CreateTexture2D = 5,
        CreateTexture3D = 6,
        CreateShaderResourceView = 7,
        CreateUnorderedAccessView = 8,
        CreateRenderTargetView = 9,
        CreateDepthStencilView = 10,
        CreateInputLayout = 11,
        CreateVertexShader = 12,
        CreateGeometryShader = 13,
        CreateGeometryShaderWithStreamOutput = 14,
        CreatePixelShader = 15,
        CreateHullShader = 16,
        CreateDomainShader = 17,
        CreateComputeShader = 18,
        CreateClassLinkage = 19,
        CreateBlendState = 20,
        CreateDepthStencilState = 21,
        CreateRasterizerState = 22,
        CreateSamplerState = 23,
        CreateQuery = 24,
        CreatePredicate = 25,
        CreateCounter = 26,
        CreateDeferredContext = 27,
        OpenSharedResource = 28,
        CheckFormatSupport = 29,
        CheckMultisampleQualityLevels = 30,
        CheckCounterInfo = 31,
        CheckCounter = 32,
        CheckFeatureSupport = 33,
        GetPrivateData = 34,
        SetPrivateData = 35,
        SetPrivateDataInterface = 36,
        GetFeatureLevel = 37,
        GetCreationFlags = 38,
        GetDeviceRemovedReason = 39,
        GetImmediateContext = 40,
        SetExceptionMode = 41,
        GetExceptionMode = 42,
    }

    /// <summary>
    /// Direct3D 11 Hook - this hooks the SwapChain.Present to take screenshots
    /// </summary>
    internal class DXHookD3D11: BaseDXHook
    {
        const int D3D11_DEVICE_METHOD_COUNT = 43;

        public DXHookD3D11(CaptureInterface ssInterface)
            : base(ssInterface)
        {
        }

        List<IntPtr> _d3d11VTblAddresses = null;
        List<IntPtr> _dxgiSwapChainVTblAddresses = null;

        Hook<DXGISwapChain_PresentDelegate> DXGISwapChain_PresentHook = null;
        Hook<DXGISwapChain_ResizeTargetDelegate> DXGISwapChain_ResizeTargetHook = null;

        object _lock = new object();

        #region Internal device resources
        SharpDX.Direct3D11.Device _device;
        SwapChain _swapChain;
        SharpDX.Windows.RenderForm _renderForm;
        Texture2D _resolvedRTShared;
        SharpDX.DXGI.KeyedMutex _resolvedRTSharedKeyedMutex;
        ShaderResourceView _resolvedSharedSRV;
        Capture.Hook.DX11.ScreenAlignedQuadRenderer _saQuad;
        Texture2D _finalRT;
        Texture2D _resizedRT;
        RenderTargetView _resizedRTV;
        #endregion

        Query _query;
        bool _queryIssued;
        bool _finalRTMapped;
        ScreenshotRequest _requestCopy;

        #region Main device resources
        Texture2D _resolvedRT;
        SharpDX.DXGI.KeyedMutex _resolvedRTKeyedMutex;
        SharpDX.DXGI.KeyedMutex _resolvedRTKeyedMutex_Dev2;
        //ShaderResourceView _resolvedSRV;
        #endregion

        protected override string HookName
        {
            get
            {
                return "DXHookD3D11";
            }
        }

        public override void Hook()
        {
            this.DebugMessage("Hook: Begin");
            if (_d3d11VTblAddresses == null)
            {
                _d3d11VTblAddresses = new List<IntPtr>();
                _dxgiSwapChainVTblAddresses = new List<IntPtr>();

                #region Get Device and SwapChain method addresses
                // Create temporary device + swapchain and determine method addresses
                _renderForm = ToDispose(new SharpDX.Windows.RenderForm());
                this.DebugMessage("Hook: Before device creation");
                SharpDX.Direct3D11.Device.CreateWithSwapChain(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    DXGI.CreateSwapChainDescription(_renderForm.Handle),
                    out _device,
                    out _swapChain);

                ToDispose(_device);
                ToDispose(_swapChain);

                if (_device != null && _swapChain != null)
                {
                    this.DebugMessage("Hook: Device created");
                    _d3d11VTblAddresses.AddRange(GetVTblAddresses(_device.NativePointer, D3D11_DEVICE_METHOD_COUNT));
                    _dxgiSwapChainVTblAddresses.AddRange(GetVTblAddresses(_swapChain.NativePointer, DXGI.DXGI_SWAPCHAIN_METHOD_COUNT));
                }
                else
                {
                    this.DebugMessage("Hook: Device creation failed");
                }
                #endregion
            }
            
            // We will capture target/window resizes here
            DXGISwapChain_ResizeTargetHook = new Hook<DXGISwapChain_ResizeTargetDelegate>(
                _dxgiSwapChainVTblAddresses[(int)DXGI.DXGISwapChainVTbl.ResizeTarget],
                new DXGISwapChain_ResizeTargetDelegate(ResizeTargetHook),
                this);

            /*
             * Don't forget that all hooks will start deactivated...
             * The following ensures that all threads are intercepted:
             * Note: you must do this for each hook.
             */
            DXGISwapChain_PresentHook.Activate();
            
            DXGISwapChain_ResizeTargetHook.Activate();

            Hooks.Add(DXGISwapChain_PresentHook);
            Hooks.Add(DXGISwapChain_ResizeTargetHook);
        }

        public override void Cleanup()
        {
            try
            {
                if (_overlayEngine != null)
                {
                    _overlayEngine.Dispose();
                    _overlayEngine = null;
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// The IDXGISwapChain.Present function definition
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        delegate int DXGISwapChain_PresentDelegate(IntPtr swapChainPtr, int syncInterval, /* int */ SharpDX.DXGI.PresentFlags flags);

        /// <summary>
        /// The IDXGISwapChain.ResizeTarget function definition
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        delegate int DXGISwapChain_ResizeTargetDelegate(IntPtr swapChainPtr, ref ModeDescription newTargetParameters);

        /// <summary>
        /// Hooked to allow resizing a texture/surface that is reused. Currently not in use as we create the texture for each request
        /// to support different sizes each time (as we use DirectX to copy only the region we are after rather than the entire backbuffer)
        /// </summary>
        /// <param name="swapChainPtr"></param>
        /// <param name="newTargetParameters"></param>
        /// <returns></returns>
        int ResizeTargetHook(IntPtr swapChainPtr, ref ModeDescription newTargetParameters)
        {
            // Dispose of overlay engine (so it will be recreated with correct renderTarget view size)
            if (_overlayEngine != null)
            {
                _overlayEngine.Dispose();
                _overlayEngine = null;
            }

            return DXGISwapChain_ResizeTargetHook.Original(swapChainPtr, ref newTargetParameters);
        }

        void EnsureResources(SharpDX.Direct3D11.Device device, Texture2DDescription description, Rectangle captureRegion, ScreenshotRequest request)
        {
            if (_device != null && request.Resize != null && (_resizedRT == null || (_resizedRT.Device.NativePointer != _device.NativePointer || _resizedRT.Description.Width != request.Resize.Value.Width || _resizedRT.Description.Height != request.Resize.Value.Height)))
            {
                // Create/Recreate resources for resizing
                RemoveAndDispose(ref _resizedRT);
                RemoveAndDispose(ref _resizedRTV);
                RemoveAndDispose(ref _saQuad);

                _resizedRT = ToDispose(new Texture2D(_device, new Texture2DDescription() {
                    Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm, // Supports BMP/PNG/etc
                    Height = request.Resize.Value.Height,
                    Width = request.Resize.Value.Width,
                    ArraySize = 1,
                    SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                    BindFlags = BindFlags.RenderTarget,
                    MipLevels = 1,
                    Usage = ResourceUsage.Default,
                    OptionFlags = ResourceOptionFlags.None
                }));

                _resizedRTV = ToDispose(new RenderTargetView(_device, _resizedRT));

                _saQuad = ToDispose(new DX11.ScreenAlignedQuadRenderer());
                _saQuad.Initialize(new DX11.DeviceManager(_device));
            }

            // Check if _resolvedRT or _finalRT require creation
            if (_finalRT != null && _finalRT.Device.NativePointer == _device.NativePointer &&
                _finalRT.Description.Height == captureRegion.Height && _finalRT.Description.Width == captureRegion.Width &&
                _resolvedRT != null && _resolvedRT.Description.Height == description.Height && _resolvedRT.Description.Width == description.Width &&
                _resolvedRT.Device.NativePointer == device.NativePointer && _resolvedRT.Description.Format == description.Format
                )
            {
                return;
            }

            RemoveAndDispose(ref _query);
            RemoveAndDispose(ref _resolvedRT);
            RemoveAndDispose(ref _resolvedSharedSRV);
            RemoveAndDispose(ref _finalRT);
            RemoveAndDispose(ref _resolvedRTShared);

            _query = new Query(_device, new QueryDescription()
            {
                Flags = QueryFlags.None,
                Type = QueryType.Event
            });
            _queryIssued = false;

            _resolvedRT = ToDispose(new Texture2D(device, new Texture2DDescription() {
                CpuAccessFlags = CpuAccessFlags.None,
                Format = description.Format, // for multisampled backbuffer, this must be same format
                Height = description.Height,
                Usage = ResourceUsage.Default,
                Width = description.Width,
                ArraySize = 1,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), // Ensure single sample
                BindFlags = BindFlags.ShaderResource,
                MipLevels = 1,
                OptionFlags = ResourceOptionFlags.SharedKeyedmutex
            }));

            // Retrieve reference to the keyed mutex
            _resolvedRTKeyedMutex = ToDispose(_resolvedRT.QueryInterfaceOrNull<SharpDX.DXGI.KeyedMutex>());

            using (var resource = _resolvedRT.QueryInterface<SharpDX.DXGI.Resource>())
            {
                _resolvedRTShared = ToDispose(_device.OpenSharedResource<Texture2D>(resource.SharedHandle));
                _resolvedRTKeyedMutex_Dev2 = ToDispose(_resolvedRTShared.QueryInterfaceOrNull<SharpDX.DXGI.KeyedMutex>());
            }

            // SRV for use if resizing
            _resolvedSharedSRV = ToDispose(new ShaderResourceView(_device, _resolvedRTShared));

            _finalRT = ToDispose(new Texture2D(_device, new Texture2DDescription()
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                Format = description.Format,
                Height = captureRegion.Height,
                Usage = ResourceUsage.Staging,
                Width = captureRegion.Width,
                ArraySize = 1,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                BindFlags = BindFlags.None,
                MipLevels = 1,
                OptionFlags = ResourceOptionFlags.None
            }));
            _finalRTMapped = false;
        }

        Capture.Hook.DX11.DXOverlayEngine _overlayEngine;

        IntPtr _swapChainPointer = IntPtr.Zero;
        
    }
}
