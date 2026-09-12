# Inject an ICO file into an exe's Win32 resources (RT_ICON + RT_GROUP_ICON).
# Usage: powershell -File tools/set_exe_icon.ps1 -Exe <path> -Ico <path>
# Note: Unity's PlayerSettings.SetIcons does not persist in batchmode, so the
#       icon is injected directly into the built exe (tray icon reads it too).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Ico
)

$src = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

public static class IconInjector
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

    const int RT_ICON = 3;
    const int RT_GROUP_ICON = 14;

    public static string Inject(string exePath, string icoPath)
    {
        if (!File.Exists(exePath)) return "exe not found: " + exePath;
        if (!File.Exists(icoPath)) return "ico not found: " + icoPath;

        byte[] ico = File.ReadAllBytes(icoPath);
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1)
            return "not a valid ICO file";

        int count = BitConverter.ToUInt16(ico, 4);
        var imageData = new List<byte[]>();
        var meta = new List<int[]>();

        for (int i = 0; i < count; i++)
        {
            int off = 6 + 16 * i;
            int size = BitConverter.ToInt32(ico, off + 8);
            int dataOff = BitConverter.ToInt32(ico, off + 12);
            if (dataOff + size > ico.Length) return "ICO entry out of range at " + i;

            var data = new byte[size];
            Array.Copy(ico, dataOff, data, 0, size);
            imageData.Add(data);
            meta.Add(new int[]
            {
                ico[off], ico[off + 1], ico[off + 2],
                BitConverter.ToUInt16(ico, off + 4),
                BitConverter.ToUInt16(ico, off + 6),
                size
            });
        }

        IntPtr h = BeginUpdateResource(exePath, false);
        if (h == IntPtr.Zero) return "BeginUpdateResource failed: " + Marshal.GetLastWin32Error();

        string error = null;
        for (int i = 0; i < imageData.Count && error == null; i++)
        {
            if (!UpdateResource(h, (IntPtr)RT_ICON, (IntPtr)(i + 1), 0, imageData[i], (uint)imageData[i].Length))
                error = "UpdateResource(RT_ICON) failed at " + i + ": " + Marshal.GetLastWin32Error();
        }

        if (error == null)
        {
            // GRPICONDIR + GRPICONDIRENTRY[]: same as ICONDIR, but last two bytes of an
            // entry are the resource ID (not the file offset).
            var group = new byte[6 + 14 * imageData.Count];
            BitConverter.GetBytes((ushort)0).CopyTo(group, 0);
            BitConverter.GetBytes((ushort)1).CopyTo(group, 2);
            BitConverter.GetBytes((ushort)imageData.Count).CopyTo(group, 4);
            for (int i = 0; i < imageData.Count; i++)
            {
                int g = 6 + 14 * i;
                var m = meta[i];
                group[g] = (byte)m[0];
                group[g + 1] = (byte)m[1];
                group[g + 2] = (byte)m[2];
                group[g + 3] = 0;
                BitConverter.GetBytes((ushort)m[3]).CopyTo(group, g + 4);
                BitConverter.GetBytes((ushort)m[4]).CopyTo(group, g + 6);
                BitConverter.GetBytes(m[5]).CopyTo(group, g + 8);
                BitConverter.GetBytes((ushort)(i + 1)).CopyTo(group, g + 12);
            }

            if (!UpdateResource(h, (IntPtr)RT_GROUP_ICON, (IntPtr)1, 0, group, (uint)group.Length))
                error = "UpdateResource(RT_GROUP_ICON) failed: " + Marshal.GetLastWin32Error();
        }

        if (!EndUpdateResource(h, false))
            return "EndUpdateResource failed: " + Marshal.GetLastWin32Error();

        return error == null ? "OK" : error;
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp
$result = [IconInjector]::Inject($Exe, $Ico)
Write-Output $result
if ($result -ne "OK") { exit 1 }
