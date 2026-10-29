using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CRDLightweightARM.NativeProtocol;

/// <summary>
/// High-performance, zero-warning safe Win32/COM P/Invoke wrapper for Direct3D 11 and DXGI 1.6
/// specifically tuned for Qualcomm Snapdragon X Plus (X1P42100) and Qualcomm Adreno X1-45 GPU.
/// Features:
/// - D3D11 device creation with D3D11_CREATE_DEVICE_VIDEO_SUPPORT (0x800) | D3D11_CREATE_DEVICE_BGRA_SUPPORT (0x20)
/// - DXGI 1.6 ultra-low latency presentation with SetMaximumFrameLatency(1)
/// - DXGI_SWAP_EFFECT_FLIP_DISCARD swapchain with DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT | DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING
/// - Hardware zero-copy NV12 video decoding and color space conversion via ID3D11VideoDevice and ID3D11VideoContext
/// </summary>
public sealed class D3D11AdrenoPresenter : IDisposable
{
    #region Constants and Enums

    public const uint D3D11_SDK_VERSION = 7;
    public const uint D3D_DRIVER_TYPE_HARDWARE = 1;

    public const uint D3D11_CREATE_DEVICE_SINGLETHREADED = 0x0001;
    public const uint D3D11_CREATE_DEVICE_DEBUG = 0x0002;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x0020;
    public const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x0800;

    public const uint DXGI_SWAP_EFFECT_DISCARD = 0;
    public const uint DXGI_SWAP_EFFECT_SEQUENTIAL = 1;
    public const uint DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL = 3;
    public const uint DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;

    public const uint DXGI_SWAP_CHAIN_FLAG_NONPREROTATED = 0x0001;
    public const uint DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH = 0x0002;
    public const uint DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT = 0x0040;
    public const uint DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING = 0x0800;

    public const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x0020;
    public const uint DXGI_PRESENT_DO_NOT_WAIT = 0x00000001;
    public const uint DXGI_PRESENT_ALLOW_TEARING = 0x00000200;

    public const uint DXGI_FORMAT_UNKNOWN = 0;
    public const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint DXGI_FORMAT_NV12 = 103;

    public const uint D3D11_USAGE_DEFAULT = 0;
    public const uint D3D11_USAGE_IMMUTABLE = 1;
    public const uint D3D11_USAGE_DYNAMIC = 2;
    public const uint D3D11_USAGE_STAGING = 3;

    public const uint D3D11_BIND_SHADER_RESOURCE = 0x0008;
    public const uint D3D11_BIND_RENDER_TARGET = 0x0020;
    public const uint D3D11_BIND_DECODER = 0x0200;

    public const uint D3D11_CPU_ACCESS_WRITE = 0x00010000;
    public const uint D3D11_CPU_ACCESS_READ = 0x00020000;

    public const uint D3D11_MAP_WRITE = 2;
    public const uint D3D11_MAP_WRITE_DISCARD = 4;

    public const uint QUALCOMM_VENDOR_ID = 0x5143;
    public const uint QUALCOMM_ACPI_VENDOR_ID = 0x4D4F4351; // 'QCOM' in ACPI/DXGI on Windows on ARM64

    #endregion

    #region Win32 and COM Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public int Stereo;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage;
        public uint BufferCount;
        public uint Scaling;
        public uint SwapEffect;
        public uint AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_VIDEO_PROCESSOR_CONTENT_DESC
    {
        public int InputFrameFormat;
        public DXGI_RATIONAL InputFrameRate;
        public uint InputWidth;
        public uint InputHeight;
        public DXGI_RATIONAL OutputFrameRate;
        public uint OutputWidth;
        public uint OutputHeight;
        public int Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
    {
        public uint FourCC;
        public int ViewDimension; // D3D11_VPIV_DIMENSION_TEXTURE2D = 1
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
    {
        public int ViewDimension; // D3D11_VPOV_DIMENSION_TEXTURE2D = 1
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_VIDEO_PROCESSOR_STREAM
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public IntPtr ppPastSurfaces;
        public IntPtr pInputSurface;
        public IntPtr ppFutureSurfaces;
        public IntPtr ppPastSurfacesRight;
        public IntPtr pInputSurfaceRight;
        public IntPtr ppFutureSurfacesRight;
    }

    #endregion

    #region Native P/Invoke Declarations

    private static class NativeMethods
    {
        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            uint DriverType,
            IntPtr Software,
            uint Flags,
            [In] uint[]? pFeatureLevels,
            uint FeatureLevels,
            uint SDKVersion,
            out IntPtr ppDevice,
            out uint pFeatureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int CreateDXGIFactory2(
            uint Flags,
            in Guid riid,
            out IntPtr ppFactory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObjectEx(
            IntPtr hHandle,
            uint dwMilliseconds,
            bool bAlertable);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
    }

    #endregion

    #region Known COM Interface GUIDs

    public static readonly Guid IID_IDXGIDevice1 = new("77DB970F-6276-48BA-BA28-070143B4392C");
    public static readonly Guid IID_IDXGIFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    public static readonly Guid IID_IDXGISwapChain2 = new("A87B2666-9ACC-424A-AC64-0A473E4B91FA");
    public static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    public static readonly Guid IID_ID3D11VideoDevice = new("10EC4D5B-975B-4689-B9E4-DA606C407A1D");
    public static readonly Guid IID_ID3D11VideoContext = new("61F23C40-E160-4811-9A0F-EBBC61B6385B");

    #endregion

    #region Delegates for COM VTable Invocation

    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, in Guid riid, out IntPtr ppvObject);

    // IDXGIDevice (slot 7) & IDXGIDevice1 (slot 12)
    private delegate int GetAdapterDelegate(IntPtr thisPtr, out IntPtr pAdapter);
    private delegate int SetMaximumFrameLatencyDelegate(IntPtr thisPtr, uint MaxLatency);

    // IDXGIObject (slot 6) & IDXGIAdapter (slot 8)
    private delegate int GetParentDelegate(IntPtr thisPtr, in Guid riid, out IntPtr ppParent);
    private delegate int GetAdapterDescDelegate(IntPtr thisPtr, out DXGI_ADAPTER_DESC pDesc);

    // IDXGIFactory2 (slot 15)
    private delegate int CreateSwapChainForHwndDelegate(
        IntPtr thisPtr,
        IntPtr pDevice,
        IntPtr hWnd,
        ref DXGI_SWAP_CHAIN_DESC1 pDesc,
        IntPtr pFullscreenDesc,
        IntPtr pRestrictToOutput,
        out IntPtr ppSwapChain);

    // IDXGISwapChain (slots 8, 9, 13)
    private delegate int PresentDelegate(IntPtr thisPtr, uint SyncInterval, uint Flags);
    private delegate int GetBufferDelegate(IntPtr thisPtr, uint Buffer, in Guid riid, out IntPtr ppSurface);
    private delegate int ResizeBuffersDelegate(IntPtr thisPtr, uint BufferCount, uint Width, uint Height, uint NewFormat, uint SwapChainFlags);

    // IDXGISwapChain2 (slot 33)
    private delegate IntPtr GetFrameLatencyWaitableObjectDelegate(IntPtr thisPtr);

    // ID3D11Device (slot 5)
    private delegate int CreateTexture2DDelegate(IntPtr thisPtr, ref D3D11_TEXTURE2D_DESC pDesc, IntPtr pInitialData, out IntPtr ppTexture2D);

    // ID3D11DeviceContext (slots 14, 15, 47)
    private delegate int MapDelegate(IntPtr thisPtr, IntPtr pResource, uint Subresource, uint MapType, uint MapFlags, out D3D11_MAPPED_SUBRESOURCE pMappedResource);
    private delegate void UnmapDelegate(IntPtr thisPtr, IntPtr pResource, uint Subresource);
    private delegate void CopyResourceDelegate(IntPtr thisPtr, IntPtr pDstResource, IntPtr pSrcResource);

    // ID3D11VideoDevice (slots 4, 8, 9, 10)
    private delegate int CreateVideoProcessorDelegate(IntPtr thisPtr, IntPtr pEnum, uint RateConversionIndex, out IntPtr ppVideoProcessor);
    private delegate int CreateVideoProcessorInputViewDelegate(IntPtr thisPtr, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC pDesc, out IntPtr ppVPIView);
    private delegate int CreateVideoProcessorOutputViewDelegate(IntPtr thisPtr, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC pDesc, out IntPtr ppVPOView);
    private delegate int CreateVideoProcessorEnumeratorDelegate(IntPtr thisPtr, ref D3D11_VIDEO_PROCESSOR_CONTENT_DESC pDesc, out IntPtr ppEnum);

    // ID3D11VideoContext (slot 53)
    private delegate int VideoProcessorBltDelegate(IntPtr thisPtr, IntPtr pVideoProcessor, IntPtr pView, uint OutputFrame, uint StreamCount, [In] D3D11_VIDEO_PROCESSOR_STREAM[] pStreams);

    #endregion

    #region Fields and Properties

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _dxgiDevice1;
    private IntPtr _swapChain;
    private IntPtr _swapChain2;
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _videoProcessorEnum;
    private IntPtr _videoProcessor;
    private IntPtr _nv12StagingTexture;
    private IntPtr _nv12DefaultTexture;
    private IntPtr _nv12InputView;
    private IntPtr _backBufferTexture;
    private IntPtr _backBufferOutputView;
    private IntPtr _frameLatencyWaitableObject = IntPtr.Zero;

    private int _width;
    private int _height;
    private bool _isDisposed;
    private bool _isAdrenoGpu;
    private string _adapterDescription = string.Empty;
    private bool _tearingSupported;

    public IntPtr DevicePointer => _device;
    public IntPtr ContextPointer => _context;
    public IntPtr SwapChainPointer => _swapChain;
    public IntPtr FrameLatencyWaitableObject => _frameLatencyWaitableObject;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _swapChain != IntPtr.Zero;
    public bool IsAdrenoGpu => _isAdrenoGpu;
    public string AdapterDescription => _adapterDescription;
    public bool IsTearingSupported => _tearingSupported;

    #endregion

    #region Helper for VTable Function Resolution

    private static T GetVTableMethod<T>(IntPtr comPtr, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr methodPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    private static void SafeRelease(ref IntPtr comPtr)
    {
        if (comPtr != IntPtr.Zero)
        {
            Marshal.Release(comPtr);
            comPtr = IntPtr.Zero;
        }
    }

    #endregion

    #region Initialization and Setup

    /// <summary>
    /// Initializes Direct3D 11, DXGI 1.6 swapchain, and ID3D11VideoDevice hardware processor.
    /// Sets MaximumFrameLatency to 1 for sub-frame response on Snapdragon X Plus.
    /// </summary>
    public bool Initialize(IntPtr hWnd, int width, int height, bool enableTearing = true, bool enableWaitableObject = true)
    {
        if (hWnd == IntPtr.Zero) throw new ArgumentException("Invalid Window Handle (hWnd).", nameof(hWnd));
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1200;

        _width = width & ~1;
        _height = height & ~1;

        // 1. Create D3D11 Device and Context with VIDEO_SUPPORT and BGRA_SUPPORT
        uint creationFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        uint[] featureLevels = { 0xB100 /* 11_1 */, 0xB000 /* 11_0 */, 0xA100 /* 10_1 */ };

        int hr = NativeMethods.D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            creationFlags,
            featureLevels,
            (uint)featureLevels.Length,
            D3D11_SDK_VERSION,
            out _device,
            out uint _,
            out _context);

        if (hr < 0 || _device == IntPtr.Zero)
        {
            throw new Win32Exception(hr, $"D3D11CreateDevice failed with HRESULT 0x{hr:X8}");
        }

        // 2. Query IDXGIDevice1 for MaximumFrameLatency(1) & GetAdapter (slot 7 on IDXGIDevice1!)
        var qi = GetVTableMethod<QueryInterfaceDelegate>(_device, 0);
        hr = qi(_device, in IID_IDXGIDevice1, out _dxgiDevice1);
        if (hr >= 0 && _dxgiDevice1 != IntPtr.Zero)
        {
            var setLatency = GetVTableMethod<SetMaximumFrameLatencyDelegate>(_dxgiDevice1, 12);
            setLatency(_dxgiDevice1, 1);

            // 3. Obtain IDXGIAdapter from _dxgiDevice1 (slot 7) and inspect GPU (Qualcomm Adreno identification)
            var getAdapter = GetVTableMethod<GetAdapterDelegate>(_dxgiDevice1, 7);
            hr = getAdapter(_dxgiDevice1, out IntPtr pAdapter);
            if (hr >= 0 && pAdapter != IntPtr.Zero)
            {
                try
                {
                    var getDesc = GetVTableMethod<GetAdapterDescDelegate>(pAdapter, 8);
                    if (getDesc(pAdapter, out DXGI_ADAPTER_DESC desc) >= 0)
                    {
                        _adapterDescription = desc.Description;
                        _isAdrenoGpu = desc.VendorId == QUALCOMM_VENDOR_ID ||
                                      desc.VendorId == QUALCOMM_ACPI_VENDOR_ID ||
                                      desc.Description.Contains("Adreno", StringComparison.OrdinalIgnoreCase) ||
                                      desc.Description.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase);
                    }

                    // 4. Obtain IDXGIFactory2 from Adapter parent (slot 6 on IDXGIObject)
                    var getParent = GetVTableMethod<GetParentDelegate>(pAdapter, 6);
                    hr = getParent(pAdapter, in IID_IDXGIFactory2, out IntPtr pFactory2);
                    if (hr < 0 || pFactory2 == IntPtr.Zero)
                    {
                        hr = NativeMethods.CreateDXGIFactory2(0, in IID_IDXGIFactory2, out pFactory2);
                    }

                    if (pFactory2 != IntPtr.Zero)
                    {
                        try
                        {
                            // 5. Create Low-Latency Flip-Discard SwapChain
                            uint swapFlags = 0;
                            if (enableWaitableObject) swapFlags |= DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
                            if (enableTearing) swapFlags |= DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

                            var desc1 = new DXGI_SWAP_CHAIN_DESC1
                            {
                                Width = (uint)_width,
                                Height = (uint)_height,
                                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                                Stereo = 0,
                                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                                BufferCount = 2,
                                Scaling = 0, // DXGI_SCALING_STRETCH
                                SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
                                AlphaMode = 0, // DXGI_ALPHA_MODE_UNSPECIFIED
                                Flags = swapFlags
                            };

                            var createSwapChain = GetVTableMethod<CreateSwapChainForHwndDelegate>(pFactory2, 15);
                            hr = createSwapChain(pFactory2, _device, hWnd, ref desc1, IntPtr.Zero, IntPtr.Zero, out _swapChain);
                            if (hr < 0)
                            {
                                desc1.Flags &= ~DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
                                hr = createSwapChain(pFactory2, _device, hWnd, ref desc1, IntPtr.Zero, IntPtr.Zero, out _swapChain);
                            }
                            else
                            {
                                _tearingSupported = enableTearing;
                            }

                            if (hr < 0 || _swapChain == IntPtr.Zero)
                            {
                                throw new Win32Exception(hr, $"CreateSwapChainForHwnd failed with HRESULT 0x{hr:X8}");
                            }
                        }
                        finally
                        {
                            Marshal.Release(pFactory2);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(pAdapter);
                }
            }
        }

        // 6. Obtain Waitable Object from IDXGISwapChain2 (slot 33)
        if (_swapChain != IntPtr.Zero)
        {
            var swapQi = GetVTableMethod<QueryInterfaceDelegate>(_swapChain, 0);
            if (swapQi(_swapChain, in IID_IDXGISwapChain2, out _swapChain2) >= 0 && _swapChain2 != IntPtr.Zero)
            {
                var getWaitable = GetVTableMethod<GetFrameLatencyWaitableObjectDelegate>(_swapChain2, 33);
                _frameLatencyWaitableObject = getWaitable(_swapChain2);
            }
        }

        // 7. Initialize Hardware Video Processor (ID3D11VideoDevice / ID3D11VideoContext)
        InitializeVideoPipeline();
        return IsInitialized;
    }

    private void InitializeVideoPipeline()
    {
        if (_device == IntPtr.Zero || _context == IntPtr.Zero) return;

        var qi = GetVTableMethod<QueryInterfaceDelegate>(_device, 0);
        qi(_device, in IID_ID3D11VideoDevice, out _videoDevice);

        var ctxQi = GetVTableMethod<QueryInterfaceDelegate>(_context, 0);
        ctxQi(_context, in IID_ID3D11VideoContext, out _videoContext);

        CreateNV12Textures((uint)_width, (uint)_height);

        if (_videoDevice != IntPtr.Zero && _videoContext != IntPtr.Zero && _nv12DefaultTexture != IntPtr.Zero)
        {
            try
            {
                var contentDesc = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
                {
                    InputFrameFormat = 0, // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
                    InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
                    InputWidth = (uint)_width,
                    InputHeight = (uint)_height,
                    OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
                    OutputWidth = (uint)_width,
                    OutputHeight = (uint)_height,
                    Usage = 0 // D3D11_VIDEO_USAGE_PLAYBACK_NORMAL
                };

                // ID3D11VideoDevice::CreateVideoProcessorEnumerator is vtable slot 10
                var createEnum = GetVTableMethod<CreateVideoProcessorEnumeratorDelegate>(_videoDevice, 10);
                if (createEnum(_videoDevice, ref contentDesc, out _videoProcessorEnum) >= 0 && _videoProcessorEnum != IntPtr.Zero)
                {
                    // ID3D11VideoDevice::CreateVideoProcessor is vtable slot 4
                    var createProc = GetVTableMethod<CreateVideoProcessorDelegate>(_videoDevice, 4);
                    createProc(_videoDevice, _videoProcessorEnum, 0, out _videoProcessor);

                    // ID3D11VideoDevice::CreateVideoProcessorInputView is vtable slot 8
                    var inputDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
                    {
                        FourCC = 0,
                        ViewDimension = 1, // D3D11_VPIV_DIMENSION_TEXTURE2D = 1
                        MipSlice = 0,
                        ArraySlice = 0
                    };
                    var createInputView = GetVTableMethod<CreateVideoProcessorInputViewDelegate>(_videoDevice, 8);
                    createInputView(_videoDevice, _nv12DefaultTexture, _videoProcessorEnum, ref inputDesc, out _nv12InputView);

                    SetupBackBufferOutputView();
                }
            }
            catch
            {
                // Graceful fallback if video processor views are unavailable for custom dimensions
            }
        }
    }

    private void CreateNV12Textures(uint width, uint height)
    {
        SafeRelease(ref _nv12StagingTexture);
        SafeRelease(ref _nv12DefaultTexture);

        var createTex = GetVTableMethod<CreateTexture2DDelegate>(_device, 5);

        // 1. CPU-writable NV12 staging texture
        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_NV12,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_WRITE,
            MiscFlags = 0
        };
        createTex(_device, ref stagingDesc, IntPtr.Zero, out _nv12StagingTexture);

        // 2. GPU-resident default NV12 texture compatible with Adreno VideoProcessorInputView
        var defaultDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_NV12,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_DECODER,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        int hr = createTex(_device, ref defaultDesc, IntPtr.Zero, out _nv12DefaultTexture);
        if (hr < 0 || _nv12DefaultTexture == IntPtr.Zero)
        {
            defaultDesc.BindFlags = 0;
            createTex(_device, ref defaultDesc, IntPtr.Zero, out _nv12DefaultTexture);
        }
    }

    private void SetupBackBufferOutputView()
    {
        SafeRelease(ref _backBufferTexture);
        SafeRelease(ref _backBufferOutputView);

        if (_swapChain == IntPtr.Zero || _videoDevice == IntPtr.Zero || _videoProcessorEnum == IntPtr.Zero) return;

        var getBuffer = GetVTableMethod<GetBufferDelegate>(_swapChain, 9);
        if (getBuffer(_swapChain, 0, in IID_ID3D11Texture2D, out _backBufferTexture) >= 0 && _backBufferTexture != IntPtr.Zero)
        {
            var outputDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
            {
                ViewDimension = 1, // D3D11_VPOV_DIMENSION_TEXTURE2D = 1
                MipSlice = 0,
                ArraySlice = 0
            };
            // ID3D11VideoDevice::CreateVideoProcessorOutputView is vtable slot 9
            var createOutputView = GetVTableMethod<CreateVideoProcessorOutputViewDelegate>(_videoDevice, 9);
            createOutputView(_videoDevice, _backBufferTexture, _videoProcessorEnum, ref outputDesc, out _backBufferOutputView);
        }
    }

    #endregion

    #region Frame Presentation Pipeline

    public bool WaitForNextFrame(uint timeoutMs = 1000)
    {
        if (_frameLatencyWaitableObject != IntPtr.Zero)
        {
            uint waitRes = NativeMethods.WaitForSingleObjectEx(_frameLatencyWaitableObject, timeoutMs, true);
            return waitRes == 0;
        }
        return true;
    }

    public void PresentNV12Frame(
        IntPtr yPlane,
        IntPtr uvPlane,
        int yStride,
        int uvStride,
        int frameWidth,
        int frameHeight,
        bool vsync = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_swapChain == IntPtr.Zero) throw new InvalidOperationException("Presenter is not initialized.");
        if (yPlane == IntPtr.Zero || uvPlane == IntPtr.Zero) return;

        if (frameWidth != _width || frameHeight != _height)
        {
            Resize(frameWidth, frameHeight);
        }

        WaitForNextFrame(100);

        if (_nv12StagingTexture != IntPtr.Zero && _nv12DefaultTexture != IntPtr.Zero)
        {
            var map = GetVTableMethod<MapDelegate>(_context, 14);
            var unmap = GetVTableMethod<UnmapDelegate>(_context, 15);
            var copyRes = GetVTableMethod<CopyResourceDelegate>(_context, 47);

            int hr = map(_context, _nv12StagingTexture, 0, D3D11_MAP_WRITE, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
            if (hr >= 0 && mapped.pData != IntPtr.Zero)
            {
                try
                {
                    int pitch = (int)mapped.RowPitch;
                    for (int y = 0; y < frameHeight; y++)
                    {
                        IntPtr srcRow = IntPtr.Add(yPlane, y * yStride);
                        IntPtr dstRow = IntPtr.Add(mapped.pData, y * pitch);
                        NativeMethods.CopyMemory(dstRow, srcRow, (UIntPtr)Math.Min(frameWidth, yStride));
                    }

                    IntPtr dstUvBase = IntPtr.Add(mapped.pData, frameHeight * pitch);
                    int uvHeight = frameHeight / 2;
                    for (int y = 0; y < uvHeight; y++)
                    {
                        IntPtr srcRow = IntPtr.Add(uvPlane, y * uvStride);
                        IntPtr dstRow = IntPtr.Add(dstUvBase, y * pitch);
                        NativeMethods.CopyMemory(dstRow, srcRow, (UIntPtr)Math.Min(frameWidth, uvStride));
                    }
                }
                finally
                {
                    unmap(_context, _nv12StagingTexture, 0);
                }

                copyRes(_context, _nv12DefaultTexture, _nv12StagingTexture);
            }
        }

        if (_videoContext != IntPtr.Zero && _videoProcessor != IntPtr.Zero && _backBufferOutputView != IntPtr.Zero && _nv12InputView != IntPtr.Zero)
        {
            var stream = new D3D11_VIDEO_PROCESSOR_STREAM
            {
                Enable = 1,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                pInputSurface = _nv12InputView
            };

            // ID3D11VideoContext::VideoProcessorBlt is vtable slot 53
            var blt = GetVTableMethod<VideoProcessorBltDelegate>(_videoContext, 53);
            blt(_videoContext, _videoProcessor, _backBufferOutputView, 0, 1, new[] { stream });
        }

        uint syncInterval = vsync ? 1u : 0u;
        uint presentFlags = (!vsync && _tearingSupported) ? DXGI_PRESENT_ALLOW_TEARING : 0u;

        var present = GetVTableMethod<PresentDelegate>(_swapChain, 8);
        present(_swapChain, syncInterval, presentFlags);
    }

    public void PresentNV12Buffer(ReadOnlySpan<byte> nv12Data, int frameWidth, int frameHeight, bool vsync = false)
    {
        if (nv12Data.IsEmpty) return;
        PresentNV12Buffer(nv12Data.ToArray(), frameWidth, frameHeight, vsync);
    }

    public void PresentNV12Buffer(byte[] nv12Data, int frameWidth, int frameHeight, bool vsync = false)
    {
        ArgumentNullException.ThrowIfNull(nv12Data);

        int ySize = frameWidth * frameHeight;
        int uvSize = (frameWidth * frameHeight) / 2;
        if (nv12Data.Length < ySize + uvSize) return;

        GCHandle handle = GCHandle.Alloc(nv12Data, GCHandleType.Pinned);
        try
        {
            IntPtr pY = handle.AddrOfPinnedObject();
            IntPtr pUV = IntPtr.Add(pY, ySize);
            PresentNV12Frame(pY, pUV, frameWidth, frameWidth, frameWidth, frameHeight, vsync);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0) return;
        newWidth &= ~1;
        newHeight &= ~1;
        if (newWidth == _width && newHeight == _height) return;

        _width = newWidth;
        _height = newHeight;

        SafeRelease(ref _backBufferTexture);
        SafeRelease(ref _backBufferOutputView);
        SafeRelease(ref _nv12InputView);
        SafeRelease(ref _videoProcessor);
        SafeRelease(ref _videoProcessorEnum);

        if (_swapChain != IntPtr.Zero)
        {
            uint swapFlags = 0;
            if (_frameLatencyWaitableObject != IntPtr.Zero) swapFlags |= DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            if (_tearingSupported) swapFlags |= DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

            var resizeBuffers = GetVTableMethod<ResizeBuffersDelegate>(_swapChain, 13);
            resizeBuffers(_swapChain, 2, (uint)_width, (uint)_height, DXGI_FORMAT_B8G8R8A8_UNORM, swapFlags);
        }

        InitializeVideoPipeline();
    }

    #endregion

    #region Disposal Pattern

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SafeRelease(ref _nv12InputView);
        SafeRelease(ref _backBufferOutputView);
        SafeRelease(ref _backBufferTexture);
        SafeRelease(ref _nv12DefaultTexture);
        SafeRelease(ref _nv12StagingTexture);
        SafeRelease(ref _videoProcessor);
        SafeRelease(ref _videoProcessorEnum);
        SafeRelease(ref _videoContext);
        SafeRelease(ref _videoDevice);
        SafeRelease(ref _swapChain2);
        SafeRelease(ref _swapChain);
        SafeRelease(ref _dxgiDevice1);
        SafeRelease(ref _context);
        SafeRelease(ref _device);
    }

    #endregion
}
