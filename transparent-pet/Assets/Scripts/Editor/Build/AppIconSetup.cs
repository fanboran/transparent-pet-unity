using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using TransparentPet.Platform;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 应用图标装配：把 tools/make_icon.py 生成的图标位图（Assets/Art/Icon/AppIcon_*.png）
    /// 导入为不压缩纹理并设为 Standalone 平台的应用图标（exe 图标 + 任务栏图标；
    /// 托盘图标由 NativeTray 从 exe 资源中取同一图标）。
    /// 菜单：TransparentPet/应用应用程序图标；GenerateAll 会顺带调用。
    /// </summary>
    public static class AppIconSetup
    {
        const string IconDir = "Assets/Art/Icon";

        /// <summary>Windows 图标标准尺寸集合（从大到小；Unity 按实际尺寸匹配槽位）</summary>
        static readonly int[] Sizes = { 256, 128, 64, 48, 32, 16 };

        [MenuItem("TransparentPet/应用应用程序图标")]
        public static void Apply()
        {
            var textures = new List<Texture2D>();
            foreach (var size in Sizes)
            {
                var path = $"{IconDir}/AppIcon_{size}.png";
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[AppIconSetup] 缺少图标文件（先跑 tools/make_icon.py）: {path}");
                    continue;
                }

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    AssetDatabase.ImportAsset(path);
                    importer = AssetImporter.GetAtPath(path) as TextureImporter;
                }
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.maxTextureSize = 256;
                    importer.SaveAndReimport();
                }

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                    textures.Add(texture);
            }

            if (textures.Count == 0)
            {
                Debug.LogWarning("[AppIconSetup] 没有任何图标可用，跳过应用图标设置");
                return;
            }

            // 用新 API（NamedBuildTarget）：旧的 SetIconsForTargetGroup 在 2022.3 会静默失败
            // （ProjectSettings 里 m_BuildTargetIcons 保持空，构建产物仍是 Unity 默认图标）
            PlayerSettings.SetIcons(NamedBuildTarget.Standalone, textures.ToArray(), IconKind.Application);

            // 关键：PlayerSettings 是内存单例，必须把序列化资产标记为脏再保存，
            // 否则 batchmode 退出时改动不会落盘（表现为 API 报成功但 ProjectSettings 仍为空）
            var settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settingsAssets != null && settingsAssets.Length > 0 && settingsAssets[0] != null)
                EditorUtility.SetDirty(settingsAssets[0]);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AppIconSetup] 已设置 {textures.Count} 个尺寸的应用图标");
        }

        /// <summary>ICO 文件（多尺寸，tools/make_icon.py 生成）</summary>
        public const string IcoPath = "Assets/Art/Icon/AppIcon.ico";

        /// <summary>
        /// 构建后把 ICO 直接注入 exe 的 Win32 资源（RT_ICON + RT_GROUP_ICON）。
        /// 为什么不用 PlayerSettings.SetIcons：batchmode 构建时该设置不落盘
        /// （ProjectSettings.asset 的 m_BuildTargetPlatformIcons 恒为空，产物仍是 Unity 默认图标），
        /// 故由构建流程在产物上直接写资源；托盘图标（NativeTray.LoadImageW 取 ID=1）同源。
        /// 失败只告警不中断构建（图标是观感增强，不影响功能）。
        /// </summary>
        public static void InjectIntoExe(string exePath)
        {
            var icoFullPath = Path.GetFullPath(IcoPath);
            if (!File.Exists(icoFullPath))
            {
                Debug.LogWarning($"[AppIconSetup] 缺少 ICO 文件（先跑 tools/make_icon.py）: {icoFullPath}");
                return;
            }
            if (!File.Exists(exePath))
            {
                Debug.LogWarning($"[AppIconSetup] 找不到构建产物，跳过图标注入: {exePath}");
                return;
            }

            var error = WriteIcons(exePath, File.ReadAllBytes(icoFullPath));
            if (error != null)
                Debug.LogWarning($"[AppIconSetup] 图标注入失败（不影响功能）: {error}");
            else
                Debug.Log("[AppIconSetup] 应用图标已注入构建产物");
        }

        // ── PE 资源写入（kernel32 UpdateResource）──

        const int RT_ICON = 3;
        const int RT_GROUP_ICON = 14;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern System.IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateResource(System.IntPtr hUpdate, System.IntPtr lpType, System.IntPtr lpName,
            ushort wLanguage, byte[] lpData, uint cbData);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(System.IntPtr hUpdate, bool discard);

        /// <summary>把 ICO 的各尺寸图像写成 RT_ICON 资源，再写 RT_GROUP_ICON 组表；返回 null 表示成功。</summary>
        static string WriteIcons(string exePath, byte[] ico)
        {
            if (ico.Length < 6 || System.BitConverter.ToUInt16(ico, 2) != 1)
                return "不是合法的 ICO 文件";

            var count = System.BitConverter.ToUInt16(ico, 4);
            var imageData = new List<byte[]>(count);
            var meta = new List<int[]>(count); // [w, h, colors, planes, bitCount, size]

            for (var i = 0; i < count; i++)
            {
                var off = 6 + 16 * i;
                var size = System.BitConverter.ToInt32(ico, off + 8);
                var dataOff = System.BitConverter.ToInt32(ico, off + 12);
                if (dataOff + size > ico.Length)
                    return $"ICO 第 {i} 项越界";

                var data = new byte[size];
                System.Array.Copy(ico, dataOff, data, 0, size);
                imageData.Add(data);
                meta.Add(new[]
                {
                    ico[off], ico[off + 1], ico[off + 2],
                    System.BitConverter.ToUInt16(ico, off + 4),
                    System.BitConverter.ToUInt16(ico, off + 6),
                    size
                });
            }

            var h = BeginUpdateResource(exePath, false);
            if (h == System.IntPtr.Zero)
                return "BeginUpdateResource 失败: " + Marshal.GetLastWin32Error();

            string error = null;
            for (var i = 0; i < imageData.Count && error == null; i++)
            {
                if (!UpdateResource(h, (System.IntPtr)RT_ICON, (System.IntPtr)(i + 1), 0,
                        imageData[i], (uint)imageData[i].Length))
                    error = $"RT_ICON 写入失败（第 {i} 项）: " + Marshal.GetLastWin32Error();
            }

            if (error == null)
            {
                // GRPICONDIR + GRPICONDIRENTRY[]：与 ICO 文件头相同，但每项末两字节是资源 ID 而非文件偏移
                var group = new byte[6 + 14 * imageData.Count];
                System.BitConverter.GetBytes((ushort)0).CopyTo(group, 0);
                System.BitConverter.GetBytes((ushort)1).CopyTo(group, 2);
                System.BitConverter.GetBytes((ushort)imageData.Count).CopyTo(group, 4);
                for (var i = 0; i < imageData.Count; i++)
                {
                    var g = 6 + 14 * i;
                    var m = meta[i];
                    group[g] = (byte)m[0];
                    group[g + 1] = (byte)m[1];
                    group[g + 2] = (byte)m[2];
                    group[g + 3] = 0;
                    System.BitConverter.GetBytes((ushort)m[3]).CopyTo(group, g + 4);
                    System.BitConverter.GetBytes((ushort)m[4]).CopyTo(group, g + 6);
                    System.BitConverter.GetBytes(m[5]).CopyTo(group, g + 8);
                    System.BitConverter.GetBytes((ushort)(i + 1)).CopyTo(group, g + 12);
                }

                if (!UpdateResource(h, (System.IntPtr)RT_GROUP_ICON, (System.IntPtr)1, 0, group, (uint)group.Length))
                    error = "RT_GROUP_ICON 写入失败: " + Marshal.GetLastWin32Error();
            }

            // UpdateResource 已失败时必须丢弃变更（discard=true）：继续提交会留下
            // "写了部分 RT_ICON 但没有组表"的损坏图标资源
            if (!EndUpdateResource(h, error != null))
                return "EndUpdateResource 失败: " + Marshal.GetLastWin32Error();

            return error;
        }
    }
}
