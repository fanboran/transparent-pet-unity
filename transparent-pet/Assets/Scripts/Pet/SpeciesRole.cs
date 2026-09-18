// ============================================================================
// SpeciesRole.cs — 物种角色（副进程）职责：窗口标题（玻璃角色据此配对 z 序）、
// 周期断言"玻璃在物种正上方"、消费玻璃角色转发的物种增删命令、发布物种数量。
// ============================================================================
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    public class SpeciesRole : MonoBehaviour
    {
        void Start()
        {
            var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(false);
            if (hwnd != System.IntPtr.Zero)
                NativeWindowStyles.SetWindowText(hwnd, RoleEnvironment.SpeciesWindowTitle);
            InvokeRepeating(nameof(AssertZOrder), 1f, 2f);
            InvokeRepeating(nameof(PublishCounts), 0.5f, 2f);
        }

        void Update()
        {
            foreach (var cmd in RoleEnvironment.DrainSpeciesCommands())
                PetManager.Instance?.ApplyCommand(cmd);
        }

        void AssertZOrder() => RoleEnvironment.PairWindowZOrder();

        void PublishCounts()
        {
            if (PetManager.Instance != null)
                RoleEnvironment.PublishSpeciesCounts(PetManager.Instance.SpeciesCountsSnapshot());
        }
    }
}
