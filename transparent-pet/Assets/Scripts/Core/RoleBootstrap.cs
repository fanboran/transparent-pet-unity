// ============================================================================
// RoleBootstrap.cs — 引导场景：按角色切到玻璃/物种场景（双窗口双进程的入口分岔）
// ============================================================================
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TransparentPet.Core
{
    /// <summary>双进程入口：同一 exe，命令行带 -species 走物种窗口，否则走玻璃窗口。</summary>
    public class RoleBootstrap : MonoBehaviour
    {
        void Awake() => SceneManager.LoadScene(RoleEnvironment.IsSpecies ? "Species" : "Glass");
    }
}
