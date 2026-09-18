// ============================================================================
// NativeDesktopDuplication.cs — DXGI Desktop Duplication 抓屏（GPU 直拷，变化驱动）
// ============================================================================
// 替代 NativeScreenCapture(BitBlt) 的品类标准路径：DuplicateOutput 从 DWM 复制
// 已合成帧——GPU 内直拷（省 CPU 搬运）、AcquireNextFrame 变化驱动等待（桌面静止
// 时挂起零开销）。帧为 B8G8R8A8（与现有 TextureFormat.BGRA32 契约一致），行序
// 首行=屏幕顶行，输出时翻转为 bottom-up（同 GetDIBits 正 biHeight 契约）。
//
// 适配器选择：混合显卡（Optimus）与多虚拟显示器环境下，DuplicateOutput 只在
// "拥有桌面输出的适配器"上成功，其余一律 DXGI_ERROR_UNSUPPORTED——故全适配器
// 扫描逐个试复制，成功者才是真桌面（DWM 所在 GPU）。
// AccessLost 自愈：全屏独占切换/睡眠唤醒/锁屏会令复制失效（可恢复），自动释放
// 重建；重建持续失败则 HasDied=true，由调用方回退 BitBlt 路径。
//
// 所有 IID 与 vtable 方法序逐一对过 external/dxsdk/Include 官方镜像（dxgi.h /
// dxgi1_2.h / d3d11.h）；互操作全流程先经 external 同款独立验证台（diag-window）
// 跑通再搬运。教训入档：凭记忆写 GUID/方法序错了 5 处。
//
// 线程契约：实例由单一工作线程独占使用（COM 设备上下文非自由线程）。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Platform
{
    /// <summary>桌面复制会话：全适配器扫描创建；Acquire 出帧；AccessLost 自愈。</summary>
    public sealed class DesktopDuplicator : IDisposable
    {
        const int S_OK = 0;
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x87A00027);
        const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x87A00026);

        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;
        const uint D3D11_USAGE_STAGING = 3;
        const uint D3D11_MAP_READ = 1;
        const uint D3D11_CPU_ACCESS_READ = 0x20000;

        // ── 结构体 ──

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime, LastUpdateTime, AccumulatedFrames;
            public long RectsCoalesced, ProtectedContentMaskedOut;
            public IntPtr pPointerInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public uint Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        // ── COM 接口（方法声明顺序 = vtable 槽位；未用方法 IntPtr 占位，只截尾不跳中）──

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void GetParent(IntPtr a, IntPtr b);
            [PreserveSig] int EnumAdapters(uint i, out IDXGIAdapter1 adapter);
            void MakeWindowAssociation(IntPtr a, IntPtr b);
            void GetWindowAssociation(IntPtr a);
            void CreateSwapChain(IntPtr a, IntPtr b, IntPtr c);
            void CreateSoftwareAdapter(IntPtr a, IntPtr b);
            [PreserveSig] int EnumAdapters1(uint i, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void GetParent(IntPtr a, IntPtr b);
            [PreserveSig] int EnumOutputs(uint i, out IDXGIOutput output);
            void GetDesc(IntPtr pDesc); // DXGI_ADAPTER_DESC 不需要，占位
            void CheckInterfaceSupport(IntPtr a, IntPtr b);
            void GetDesc1(IntPtr pDesc);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput
        {
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void GetParent(IntPtr a, IntPtr b);
            void GetDesc(out DXGI_OUTPUT_DESC pDesc);
            void GetDisplayModeList(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void FindClosestMatchingMode(IntPtr a, IntPtr b, IntPtr c);
            void WaitForVBlank();
            [PreserveSig] int TakeOwnership(IntPtr device, IntPtr blocking);
            void ReleaseOwnership();
            void GetGammaControlCapabilities(IntPtr a);
            void SetGammaControl(IntPtr a);
            void GetGammaControl(IntPtr a);
            void SetDisplaySurface(IntPtr a);
            void GetDisplaySurfaceData(IntPtr a);
            void GetFrameStatistics(IntPtr a);
        }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput1
        {
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void GetParent(IntPtr a, IntPtr b);
            void GetDesc(out DXGI_OUTPUT_DESC pDesc);
            void GetDisplayModeList(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void FindClosestMatchingMode(IntPtr a, IntPtr b, IntPtr c);
            void WaitForVBlank();
            [PreserveSig] int TakeOwnership(IntPtr device, IntPtr blocking);
            void ReleaseOwnership();
            void GetGammaControlCapabilities(IntPtr a);
            void SetGammaControl(IntPtr a);
            void GetGammaControl(IntPtr a);
            void SetDisplaySurface(IntPtr a);
            void GetDisplaySurfaceData(IntPtr a);
            void GetFrameStatistics(IntPtr a);
            void GetDisplayModeList1(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void FindClosestMatchingMode1(IntPtr a, IntPtr b, IntPtr c);
            void GetDisplaySurfaceData1(IntPtr a);
            [PreserveSig] int DuplicateOutput(ID3D11Device pDevice, out IDXGIOutputDuplication ppOutputDuplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutputDuplication
        {
            void GetDesc(IntPtr pDesc);
            [PreserveSig] int AcquireNextFrame(int timeoutMs, out DXGI_OUTDUPL_FRAME_INFO pFrameInfo, out IDXGIResource ppDesktopResource);
            [PreserveSig] int ReleaseFrame();
        }

        [ComImport, Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIResource
        {
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void GetParent(IntPtr a, IntPtr b);
            void GetSharedHandle(IntPtr a);
            void GetUsage(IntPtr a);
            void SetEvictionPriority(IntPtr a);
            void GetEvictionPriority(IntPtr a);
        }

        [ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11Texture2D
        {
            // ID3D11DeviceChild
            void GetDevice(IntPtr ppDevice);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            // ID3D11Texture2D
            void GetDesc(out D3D11_TEXTURE2D_DESC pDesc);
        }

        [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11Device
        {
            void CreateBuffer(IntPtr a, IntPtr b, IntPtr c);
            void CreateTexture1D(IntPtr a, IntPtr b, IntPtr c);
            void CreateTexture2D(ref D3D11_TEXTURE2D_DESC pDesc, IntPtr pInitialData, out ID3D11Texture2D ppTexture2D);
            void CreateTexture3D(IntPtr a, IntPtr b, IntPtr c);
            void CreateShaderResourceView(IntPtr a, IntPtr b, IntPtr c);
            void CreateUnorderedAccessView(IntPtr a, IntPtr b, IntPtr c);
            void CreateRenderTargetView(IntPtr a, IntPtr b, IntPtr c);
            void CreateDepthStencilView(IntPtr a, IntPtr b, IntPtr c);
            void CreateInputLayout(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
            void CreateVertexShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void CreateGeometryShader(IntPtr a, IntPtr b, IntPtr c);
            void CreateGeometryShaderWithStreamOutput(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i);
            void CreatePixelShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void CreateHullShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void CreateDomainShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void CreateComputeShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void CreateClassLinkage(IntPtr a);
            void CreateDeferredContext(IntPtr a, IntPtr b);
            void OpenSharedResource(IntPtr a, IntPtr b, IntPtr c);
            void CheckFormatSupport(IntPtr a, IntPtr b);
            void CheckMultisampleQualityLevels(IntPtr a, IntPtr b, IntPtr c);
            void CheckCounterInfo(IntPtr a);
            void CheckCounter(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
            void CheckFeatureSupport(IntPtr a, IntPtr b, IntPtr c);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            void GetFeatureLevel(IntPtr a);
            void GetCreationFlags(IntPtr a);
            void GetDeviceRemovedReason(IntPtr a);
            void GetImmediateContext(out ID3D11DeviceContext ppImmediateContext);
            void SetExceptionMode(IntPtr a);
            void GetExceptionMode(IntPtr a);
        }

        [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11DeviceContext
        {
            // ID3D11DeviceChild
            void GetDevice(IntPtr ppDevice);
            void GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            void SetPrivateDataInterface(IntPtr a, IntPtr b);
            // ID3D11DeviceContext
            void VSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c);
            void PSSetShaderResources(IntPtr a, IntPtr b, IntPtr c);
            void PSSetShader(IntPtr a, IntPtr b, IntPtr c);
            void PSSetSamplers(IntPtr a, IntPtr b, IntPtr c);
            void VSSetShader(IntPtr a, IntPtr b, IntPtr c);
            void DrawIndexed(IntPtr a, IntPtr b, IntPtr c);
            void Draw(IntPtr a, IntPtr b);
            [PreserveSig] int Map(ID3D11Texture2D pResource, uint Subresource, uint MapType, uint MapFlags, out D3D11_MAPPED_SUBRESOURCE pMappedResource);
            void Unmap(ID3D11Texture2D pResource, uint Subresource);
            void PSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c);
            void IASetInputLayout(IntPtr a);
            void IASetVertexBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
            void IASetIndexBuffer(IntPtr a, IntPtr b, IntPtr c);
            void DrawIndexedInstanced(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
            void DrawInstanced(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void GSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c);
            void GSSetShader(IntPtr a, IntPtr b, IntPtr c);
            void IASetPrimitiveTopology(IntPtr a);
            void VSSetShaderResources(IntPtr a, IntPtr b, IntPtr c);
            void VSSetSamplers(IntPtr a, IntPtr b, IntPtr c);
            void Begin(IntPtr a);
            void End(IntPtr a);
            void GetData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
            void SetPredication(IntPtr a, IntPtr b);
            void GSSetShaderResources(IntPtr a, IntPtr b, IntPtr c);
            void GSSetSamplers(IntPtr a, IntPtr b, IntPtr c);
            void OMSetRenderTargets(IntPtr a, IntPtr b, IntPtr c);
            void OMSetRenderTargetsAndUnorderedAccessViews(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
            void OMSetBlendState(IntPtr a, IntPtr b, IntPtr c);
            void OMSetDepthStencilState(IntPtr a, IntPtr b);
            void SOSetTargets(IntPtr a, IntPtr b, IntPtr c);
            void DrawAuto();
            void DrawIndexedInstancedIndirect(IntPtr a, IntPtr b);
            void DrawInstancedIndirect(IntPtr a, IntPtr b);
            void Dispatch(IntPtr a, IntPtr b, IntPtr c);
            void DispatchIndirect(IntPtr a, IntPtr b);
            void RSSetState(IntPtr a);
            void RSSetViewports(IntPtr a, IntPtr b);
            void RSSetScissorRects(IntPtr a, IntPtr b);
            void CopySubresourceRegion(IntPtr pDstResource, uint DstSubresource, uint DstX, uint DstY, uint DstZ, IntPtr pSrcResource, uint SrcSubresource, IntPtr pSrcBox);
            void CopyResource(ID3D11Texture2D pDstResource, ID3D11Texture2D pSrcResource);
        }

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        static extern int D3D11CreateDevice(
            IDXGIAdapter1 pAdapter, uint DriverType, IntPtr Software, uint Flags,
            IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out ID3D11Device ppDevice, out uint pFeatureLevel, out ID3D11DeviceContext ppImmediateContext);

        // ── 会话状态（单线程独占）──

        IDXGIAdapter1 adapter;
        ID3D11Device device;
        ID3D11DeviceContext context;
        IDXGIOutputDuplication duplication;
        ID3D11Texture2D staging;
        uint stagingW, stagingH;

        /// <summary>复制会话已不可恢复（重建持续失败）——调用方应回退 BitBlt 路径。</summary>
        public bool HasDied { get; private set; }

        /// <summary>当前输出尺寸（宽，物理像素）。</summary>
        public int Width { get; private set; }

        /// <summary>当前输出尺寸（高，物理像素）。</summary>
        public int Height { get; private set; }

        DesktopDuplicator() { }

        /// <summary>
        /// 全适配器扫描创建主屏复制会话：对每个"含 (0,0) 已连接输出"的适配器建
        /// D3D11 设备试复制，第一个成功者即 DWM 所在 GPU。全部失败返回 null
        ///（混合显卡/虚拟显示器驱动的环境下可能发生——BitBlt 回退的理由）。
        /// </summary>
        public static DesktopDuplicator TryCreatePrimary()
        {
            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref factoryGuid, out var factory) != 0)
                return null;

            for (uint ai = 0; ; ai++)
            {
                if (factory.EnumAdapters1(ai, out var adapter) != 0)
                    break;
                for (uint oi = 0; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var output) != 0)
                        break;
                    output.GetDesc(out var desc);
                    if (desc.AttachedToDesktop == 0 || desc.DesktopCoordinates.Left != 0 || desc.DesktopCoordinates.Top != 0)
                        continue;

                    var session = TryCreateOn(adapter, output, desc);
                    if (session != null)
                    {
                        Debug.Log($"[Duplication] 桌面复制会话建立成功：{desc.DeviceName} {session.Width}x{session.Height}");
                        return session;
                    }
                }
            }
            Debug.Log("[Duplication] 全部适配器上 DuplicateOutput 均不可用，走 BitBlt 回退");
            return null;
        }

        static DesktopDuplicator TryCreateOn(IDXGIAdapter1 adapter, IDXGIOutput output, DXGI_OUTPUT_DESC desc)
        {
            // D3D_DRIVER_TYPE_UNKNOWN(0)：传适配器时必须为它（传 4=SOFTWARE 会 E_INVALIDARG）
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context) != 0)
                return null;

            var output1 = (IDXGIOutput1)output;
            if (output1.DuplicateOutput(device, out var duplication) != S_OK)
                return null; // 非 DWM 适配器（Optimus）/虚拟显示器 → UNSUPPORTED

            return new DesktopDuplicator
            {
                adapter = adapter,
                device = device,
                context = context,
                duplication = duplication,
                Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top,
            };
        }

        /// <summary>
        /// 取下一帧（阻塞至多 timeoutMs 等 DWM 新帧）。有新帧：填充 buffer（bottom-up
        /// BGRA，同 NativeScreenCapture 契约）并返回 true；无新帧（桌面静止）或超时
        /// 返回 false（调用方沿用上一帧）。AccessLost 自动重建；重建失败置 HasDied。
        /// </summary>
        public bool TryAcquireInto(byte[] buffer, int timeoutMs = 200)
        {
            var hr = duplication.AcquireNextFrame(timeoutMs, out _, out var resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                return false;
            if (hr == DXGI_ERROR_ACCESS_LOST)
                return RebuildAfterAccessLost();
            if (hr != S_OK)
                return false;

            try
            {
                var texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                var unk = Marshal.GetIUnknownForObject(resource);
                Marshal.QueryInterface(unk, ref texGuid, out var texPtr);
                Marshal.Release(unk);
                var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
                try
                {
                    texture.GetDesc(out var td);

                    if (staging == null || stagingW != td.Width || stagingH != td.Height)
                    {
                        if (staging != null) Marshal.ReleaseComObject(staging);
                        var sdesc = new D3D11_TEXTURE2D_DESC
                        {
                            Width = td.Width, Height = td.Height,
                            MipLevels = 1, ArraySize = 1,
                            Format = td.Format,
                            SampleCount = 1, SampleQuality = 0,
                            Usage = D3D11_USAGE_STAGING, BindFlags = 0,
                            CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0,
                        };
                        device.CreateTexture2D(ref sdesc, IntPtr.Zero, out staging);
                        stagingW = td.Width;
                        stagingH = td.Height;
                        Width = (int)td.Width;
                        Height = (int)td.Height;
                    }

                    context.CopyResource(staging, texture);
                    if (context.Map(staging, 0, D3D11_MAP_READ, 0, out var mapped) != S_OK)
                        return false;
                    try
                    {
                        // 翻行序：源首行=屏幕顶行（RowPitch 步进），目标首行=屏幕底行
                        var rowBytes = stagingW * 4;
                        for (var y = 0; y < stagingH; y++)
                        {
                            var src = (IntPtr)((long)mapped.pData + y * mapped.RowPitch);
                            Marshal.Copy(src, buffer, (int)((stagingH - 1 - y) * rowBytes), (int)rowBytes);
                        }
                        return true;
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(texture);
                }
            }
            finally
            {
                duplication.ReleaseFrame();
                Marshal.ReleaseComObject(resource);
            }
        }

        bool RebuildAfterAccessLost()
        {
            // 全屏独占切换/睡眠唤醒/锁屏 → 复制失效。释放旧会话重建（Output 的
            // DuplicateOutput 可再次成功）；尺寸也可能变了（分辨率切换）。
            DisposeCore();
            var rebuilt = TryCreatePrimary();
            if (rebuilt == null)
            {
                HasDied = true;
                Debug.LogWarning("[Duplication] AccessLost 后重建失败，标记死亡（调用方回退 BitBlt）");
                return false;
            }
            // 把重建会话的状态搬进本实例（调用方持有的引用不变）
            adapter = rebuilt.adapter;
            device = rebuilt.device;
            context = rebuilt.context;
            duplication = rebuilt.duplication;
            staging = rebuilt.staging;
            stagingW = rebuilt.stagingW;
            stagingH = rebuilt.stagingH;
            Width = rebuilt.Width;
            Height = rebuilt.Height;
            Debug.Log("[Duplication] AccessLost 自愈：复制会话已重建");
            return false; // 本轮无帧，下一轮继续
        }

        public void Dispose()
        {
            DisposeCore();
            HasDied = true;
        }

        void DisposeCore()
        {
            if (staging != null) { Marshal.ReleaseComObject(staging); staging = null; }
            if (duplication != null) { Marshal.ReleaseComObject(duplication); duplication = null; }
            if (context != null) { Marshal.ReleaseComObject(context); context = null; }
            if (device != null) { Marshal.ReleaseComObject(device); device = null; }
            if (adapter != null) { Marshal.ReleaseComObject(adapter); adapter = null; }
        }
    }
}
