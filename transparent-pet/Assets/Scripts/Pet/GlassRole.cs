// ============================================================================
// GlassRole.cs — 玻璃角色（主进程）职责：拉起物种副进程；把设置窗口里
// 非玻璃物种的增删命令转发给物种副进程（跨进程命令文件）。玻璃自身的
// 增删/观感/隐形仍在本进程内闭环。
// ============================================================================
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    public class GlassRole : MonoBehaviour
    {
        void Start() => RoleEnvironment.EnsureSpeciesProcess();

        void Update()
        {
            // 设置窗口(本进程)的物种键:0=液态玻璃自己处理,其余转发给副进程
            while (NativeSettingsWindow.ManagerChanges.TryDequeue(out var change))
            {
                var sep = change.Key.IndexOf(':');
                if (sep > 0 && int.TryParse(change.Key.Substring(sep + 1), out var species)
                    && species != 0)
                {
                    RoleEnvironment.SendSpeciesCommand(
                        (change.Value > 0 ? "add:" : "remove:") + species);
                }
            }
        }
    }
}
