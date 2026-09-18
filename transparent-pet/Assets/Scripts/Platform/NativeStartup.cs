using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Debug = UnityEngine.Debug;

namespace TransparentPet.Platform
{
    /// <summary>
    /// 开机自启：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的
    /// "TransparentPet" 键值（当前用户级，无需管理员权限）。
    /// 用 advapi32 P/Invoke 而非 Microsoft.Win32.Registry：asmdef 程序集默认不引用
    /// 该 .NET 程序集，且本项目 Win32 互操作本就集中在 Core 模块。
    /// 注意事项：
    /// - 仅 Windows 有意义；本文件不做平台宏隔离（调用方处于 Player only 语境）。
    /// - 编辑器下调用会把开发机注册表写上 Unity 编辑器 exe 的路径——正式版调用方
    ///   应自行在 UNITY_EDITOR 下跳过（spike 阶段由启动流程直接调用，可接受）。
    /// - 所有失败（注册表拒绝访问、取 exe 路径失败等）只 LogWarning 不抛，
    ///   保证调用方不因自启功能异常而崩溃。
    /// </summary>
    public static class NativeStartup
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TransparentPet";

        const ulong HKEY_CURRENT_USER = 0x80000001UL;
        const uint KEY_QUERY_VALUE = 0x0001;
        const uint KEY_SET_VALUE = 0x0002;
        const uint REG_SZ = 1;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(UIntPtr hKey, string subKey, uint options, uint desiredAccess, out IntPtr resultKey);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out uint type, StringBuilder data, ref uint dataLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegSetValueExW(IntPtr hKey, string valueName, uint reserved, uint type, byte[] data, uint dataLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegDeleteValueW(IntPtr hKey, string valueName);

        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(IntPtr hKey);

        /// <summary>自启键是否存在（读注册表；读失败视为未启用，不抛异常）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                if (RegOpenKeyExW((UIntPtr)HKEY_CURRENT_USER, RunKeyPath, 0, KEY_QUERY_VALUE, out var key) != 0)
                    return false;
                try
                {
                    var buffer = new StringBuilder(512);
                    uint length = (uint)buffer.Capacity;
                    return RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, buffer, ref length) == 0;
                }
                finally
                {
                    RegCloseKey(key);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NativeStartup] 读取开机自启状态失败: " + e.Message);
                return false;
            }
        }

        /// <summary>写/删自启键。值为当前 exe 完整路径（带引号，兼容含空格的目录）。</summary>
        public static void SetStartup(bool enable)
        {
            try
            {
                if (RegOpenKeyExW((UIntPtr)HKEY_CURRENT_USER, RunKeyPath, 0, KEY_SET_VALUE, out var key) != 0)
                {
                    Debug.LogWarning("[NativeStartup] 打开 Run 注册表键失败，开机自启未设置");
                    return;
                }
                try
                {
                    if (enable)
                    {
                        // MainModule 在部分受限宿主下会抛异常（InvalidOperationException/Win32Exception），
                        // 由外层 catch 兜住，只告警不阻断
                        var exePath = Process.GetCurrentProcess().MainModule.FileName;
                        var value = "\"" + exePath + "\"";
                        var bytes = new byte[Encoding.Unicode.GetByteCount(value) + 2]; // +2 = 宽字符终止符
                        Encoding.Unicode.GetBytes(value, 0, value.Length, bytes, 0);
                        RegSetValueExW(key, ValueName, 0, REG_SZ, bytes, (uint)bytes.Length);
                    }
                    else
                    {
                        RegDeleteValueW(key, ValueName); // 键不存在返回 2，幂等忽略
                    }
                }
                finally
                {
                    RegCloseKey(key);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NativeStartup] 设置开机自启(" + enable + ")失败: " + e.Message);
            }
        }
    }
}
